using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace BugQuest
{
	internal static class BQUnhandledExceptionBridge
	{
		private const string BugQuestNodePath = "/root/BugQuest";
		private const string LogPrefix = "[BQ][CSharpBridge]";
		private const string BridgeVersion = "2026-05-06c";
		private const int FirstChanceDebugLimit = 40;
		private const long FirstChancePromotionWindowMs = 3000;
		private const int MaxTrackedFirstChanceKeys = 128;
		private const int MaxPendingManagedReports = 128;
		private const int MaxManagedReportAttempts = 600;

		private static int _installed = 0;
		private static int _firstChanceDebugCount = 0;
		private static int _mainThreadId = 0;
		private static int _unloadCleanupRegistered = 0;
		private static int _unloading = 0;
		private static Delegate _godotUnhandledExceptionDelegate;
		private static EventInfo _godotUnhandledExceptionEvent;
		private static SynchronizationContext _godotSynchronizationContext;

		private static readonly object _firstChanceLock = new object();
		private static readonly ConcurrentQueue<PendingManagedReport> _pendingManagedReports = new ConcurrentQueue<PendingManagedReport>();
		private static readonly Dictionary<string, FirstChanceState> _firstChanceStates = new Dictionary<string, FirstChanceState>();

		private sealed class PendingManagedReport
		{
			public string Message;
			public string StackTrace;
			public string Source;
			public bool IsTerminating;
			public int Attempts;
		}

		private sealed class FirstChanceState
		{
			public int Count;
			public long LastSeenMs;
			public bool Promoted;
		}

		#pragma warning disable CA2255
		[ModuleInitializer]
		internal static void InitializeModule()
		{
			try
			{
				DebugLog("[WAIT] Module initializer running (v" + BridgeVersion + ")");
				InstallIfNeeded();
				DebugLog("[OK] Module initializer complete");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Module initializer exception: " + ex);
			}
		}
		#pragma warning restore CA2255

		private static void InstallIfNeeded()
		{
			MarkGodotMainThread();
			CaptureGodotSynchronizationContext();
			RegisterUnloadCleanup();

			if (Interlocked.Exchange(ref _installed, 1) == 1)
			{
				DebugLog("[WAIT] Exception handlers already installed");
				return;
			}

			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
			DebugLog("[OK] Registered AppDomain, TaskScheduler, and FirstChance exception handlers");

			InstallGodotUnhandledExceptionHook();
		}

		private static void RegisterUnloadCleanup()
		{
			if (Interlocked.Exchange(ref _unloadCleanupRegistered, 1) == 1)
			{
				return;
			}

			try
			{
				AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(typeof(BQUnhandledExceptionBridge).Assembly);
				if (loadContext == null)
				{
					DebugError("[FAIL] Could not get bridge AssemblyLoadContext for unload cleanup");
					return;
				}

				loadContext.Unloading += OnAssemblyLoadContextUnloading;
				DebugLog("[OK] Registered AssemblyLoadContext unload cleanup");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Failed to register AssemblyLoadContext unload cleanup: " + ex.Message);
			}
		}

		private static void OnAssemblyLoadContextUnloading(AssemblyLoadContext context)
		{
			CleanupForAssemblyUnload();
		}

		private static void CleanupForAssemblyUnload()
		{
			if (Interlocked.Exchange(ref _unloading, 1) == 1)
			{
				return;
			}

			try
			{
				AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
				TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
				AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
				DebugLog("[OK] Unregistered AppDomain, TaskScheduler, and FirstChance exception handlers");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Failed unregistering managed exception handlers: " + ex.Message);
			}

			UninstallGodotUnhandledExceptionHook();
			ClearPendingManagedReports();

			lock (_firstChanceLock)
			{
				_firstChanceStates.Clear();
			}

			_godotSynchronizationContext = null;
			Interlocked.Exchange(ref _installed, 0);
		}

		private static void InstallGodotUnhandledExceptionHook()
		{
			try
			{
				if (_godotUnhandledExceptionDelegate != null)
				{
					DebugLog("[WAIT] ExceptionUtils.UnhandledException handler already registered");
					return;
				}

				Assembly godotAssembly = typeof(GD).Assembly;
				Type exceptionUtilsType = godotAssembly.GetType("Godot.NativeInterop.ExceptionUtils");
				if (exceptionUtilsType == null)
				{
					DebugLog("[WAIT] Godot.NativeInterop.ExceptionUtils type not found");
					DiscoverGodotUnhandledExceptionCandidates(godotAssembly);
					return;
				}

				EventInfo unhandledEvent = exceptionUtilsType.GetEvent(
					"UnhandledException",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

				if (unhandledEvent == null)
				{
					DebugLog("[WAIT] ExceptionUtils.UnhandledException event not found");
					DiscoverGodotUnhandledExceptionCandidates(godotAssembly);
					return;
				}

				MethodInfo handlerMethod = typeof(BQUnhandledExceptionBridge).GetMethod(
					nameof(OnGodotUnhandledException),
					BindingFlags.NonPublic | BindingFlags.Static);

				if (handlerMethod == null)
				{
					DebugError("[FAIL] Could not find OnGodotUnhandledException method");
					return;
				}

				Delegate handlerDelegate = Delegate.CreateDelegate(
					unhandledEvent.EventHandlerType,
					handlerMethod,
					throwOnBindFailure: false);

				if (handlerDelegate == null)
				{
					DebugError("[FAIL] Could not bind delegate for ExceptionUtils.UnhandledException (handler type: " + unhandledEvent.EventHandlerType + ")");
					return;
				}

				MethodInfo addMethod = unhandledEvent.GetAddMethod(nonPublic: true);
				if (addMethod == null)
				{
					DebugError("[FAIL] Could not get add accessor for ExceptionUtils.UnhandledException");
					return;
				}

				addMethod.Invoke(null, new object[] { handlerDelegate });
				_godotUnhandledExceptionDelegate = handlerDelegate;
				_godotUnhandledExceptionEvent = unhandledEvent;
				DebugLog("[OK] Registered ExceptionUtils.UnhandledException handler");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Failed to register ExceptionUtils.UnhandledException handler: " + ex);
			}
		}

		private static void UninstallGodotUnhandledExceptionHook()
		{
			try
			{
				if (_godotUnhandledExceptionDelegate == null || _godotUnhandledExceptionEvent == null)
				{
					return;
				}

				MethodInfo removeMethod = _godotUnhandledExceptionEvent.GetRemoveMethod(nonPublic: true);
				if (removeMethod == null)
				{
					DebugError("[FAIL] Could not get remove accessor for ExceptionUtils.UnhandledException");
					return;
				}

				removeMethod.Invoke(null, new object[] { _godotUnhandledExceptionDelegate });
				DebugLog("[OK] Unregistered ExceptionUtils.UnhandledException handler");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Failed to unregister ExceptionUtils.UnhandledException handler: " + ex.Message);
			}
			finally
			{
				_godotUnhandledExceptionDelegate = null;
				_godotUnhandledExceptionEvent = null;
			}
		}

		private static void DiscoverGodotUnhandledExceptionCandidates(Assembly godotAssembly)
		{
			try
			{
				Type[] types = godotAssembly.GetTypes();
				for (int t = 0; t < types.Length; t++)
				{
					Type type = types[t];
					if (type == null)
					{
						continue;
					}

					EventInfo[] events = type.GetEvents(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
					for (int i = 0; i < events.Length; i++)
					{
						EventInfo eventInfo = events[i];
						if (eventInfo == null || eventInfo.Name == null)
						{
							continue;
						}

						if (eventInfo.Name.IndexOf("UnhandledException", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							string handlerTypeName = eventInfo.EventHandlerType == null ? "null" : eventInfo.EventHandlerType.FullName;
							DebugLog("[WAIT] Candidate event: " + type.FullName + "." + eventInfo.Name + " (handler=" + handlerTypeName + ")");
						}
					}

					MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
					for (int i = 0; i < methods.Length; i++)
					{
						MethodInfo methodInfo = methods[i];
						if (methodInfo == null || methodInfo.Name == null)
						{
							continue;
						}

						if (methodInfo.Name.IndexOf("UnhandledException", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							DebugLog("[WAIT] Candidate method: " + type.FullName + "." + methodInfo.Name);
						}
					}
				}
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Failed discovering unhandled exception candidates: " + ex.Message);
			}
		}

		private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs args)
		{
			try
			{
				if (args == null || args.Exception == null)
				{
					return;
				}

				Exception exception = args.Exception;
				string stackTrace = exception.StackTrace;
				string safeStackTrace = stackTrace ?? string.Empty;

				if (safeStackTrace.IndexOf(nameof(BQUnhandledExceptionBridge), StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return;
				}

				bool fromLogUnhandled = safeStackTrace.IndexOf("Godot.NativeInterop.ExceptionUtils.LogUnhandledException", StringComparison.OrdinalIgnoreCase) >= 0;
				bool fromScriptBridge = safeStackTrace.IndexOf("Godot.Bridge.ScriptManagerBridge", StringComparison.OrdinalIgnoreCase) >= 0;
				bool fromCSharpInstanceBridge = safeStackTrace.IndexOf("Godot.Bridge.CSharpInstanceBridge.Call", StringComparison.OrdinalIgnoreCase) >= 0;
				bool fromGeneratedInvoke = safeStackTrace.IndexOf(".InvokeGodotClassMethod(", StringComparison.OrdinalIgnoreCase) >= 0;
				bool hasSignalCallbackInStack = safeStackTrace.IndexOf("_on_", StringComparison.OrdinalIgnoreCase) >= 0;
				bool hasSignalCallbackInTargetSite = exception.TargetSite != null &&
													 exception.TargetSite.Name != null &&
													 exception.TargetSite.Name.IndexOf("_on_", StringComparison.OrdinalIgnoreCase) >= 0;
				bool hasSignalCallback = hasSignalCallbackInStack || hasSignalCallbackInTargetSite;
				bool fromGodotManagedCallback = fromScriptBridge || fromCSharpInstanceBridge || fromGeneratedInvoke;
				bool candidateByTargetSite = hasSignalCallbackInTargetSite;

				bool candidate = fromLogUnhandled || (fromGodotManagedCallback && hasSignalCallback) || candidateByTargetSite;
				if (!candidate)
				{
					return;
				}

				string firstFrame = GetFirstStackFrame(safeStackTrace);
				string key = BuildFirstChanceKey(exception, firstFrame);
				long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

				bool shouldPromote = ShouldPromoteFirstChance(key, fromLogUnhandled, candidateByTargetSite, nowMs, out int seenCount);

				int logIndex = Interlocked.Increment(ref _firstChanceDebugCount);
				if (logIndex <= FirstChanceDebugLimit)
				{
					DebugError("[WAIT] FirstChance exception observed. type=" + exception.GetType().FullName +
								   ", message=" + exception.Message +
								   ", fromLogUnhandled=" + fromLogUnhandled +
								   ", fromGodotManagedCallback=" + fromGodotManagedCallback +
								   ", candidateByTargetSite=" + candidateByTargetSite +
								   ", targetSite=" + (exception.TargetSite == null ? "<null>" : exception.TargetSite.Name) +
								   ", seenCount=" + seenCount +
								   ", key=" + key +
								   ", frame=" + firstFrame);
				}

				if (!shouldPromote)
				{
					return;
				}

				DebugError("[WAIT] Promoting FirstChance exception to BugQuest report");
				ReportManagedException(exception, false, "AppDomain.FirstChanceException");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] OnFirstChanceException callback failed: " + ex.Message);
			}
		}

		private static bool ShouldPromoteFirstChance(string key, bool fromLogUnhandled, bool candidateByTargetSite, long nowMs, out int seenCount)
		{
			lock (_firstChanceLock)
			{
				if (!_firstChanceStates.TryGetValue(key, out FirstChanceState state))
				{
					state = new FirstChanceState();
					_firstChanceStates[key] = state;
				}

				if (nowMs - state.LastSeenMs > FirstChancePromotionWindowMs)
				{
					state.Count = 0;
					state.Promoted = false;
				}

				state.Count++;
				state.LastSeenMs = nowMs;
				seenCount = state.Count;

				if (_firstChanceStates.Count > MaxTrackedFirstChanceKeys)
				{
					PruneFirstChanceState(nowMs);
				}

				if (state.Promoted)
				{
					return false;
				}

				bool shouldPromote = fromLogUnhandled || candidateByTargetSite || state.Count >= 2;
				if (shouldPromote)
				{
					state.Promoted = true;
				}

				return shouldPromote;
			}
		}

		private static void PruneFirstChanceState(long nowMs)
		{
			List<string> staleKeys = new List<string>();
			foreach (KeyValuePair<string, FirstChanceState> pair in _firstChanceStates)
			{
				if (nowMs - pair.Value.LastSeenMs > FirstChancePromotionWindowMs)
				{
					staleKeys.Add(pair.Key);
				}
			}

			for (int i = 0; i < staleKeys.Count; i++)
			{
				_firstChanceStates.Remove(staleKeys[i]);
			}
		}

		private static string BuildFirstChanceKey(Exception exception, string firstFrame)
		{
			string typeName = exception.GetType().FullName;
			if (string.IsNullOrEmpty(typeName))
			{
				typeName = exception.GetType().Name;
			}

			string targetSiteName = "<null>";
			if (exception.TargetSite != null && !string.IsNullOrEmpty(exception.TargetSite.Name))
			{
				targetSiteName = exception.TargetSite.Name;
			}

			string normalizedFrame = NormalizeFrameForKey(firstFrame);
			return typeName + "|" + exception.Message + "|" + targetSiteName + "|" + normalizedFrame;
		}

		private static string NormalizeFrameForKey(string frame)
		{
			if (string.IsNullOrEmpty(frame))
			{
				return "<empty-frame>";
			}

			string normalized = frame.Trim();
			int locationIndex = normalized.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
			if (locationIndex >= 0)
			{
				normalized = normalized.Substring(0, locationIndex).Trim();
			}

			int lineIndex = normalized.IndexOf(":line ", StringComparison.OrdinalIgnoreCase);
			if (lineIndex >= 0)
			{
				normalized = normalized.Substring(0, lineIndex).Trim();
			}

			return normalized;
		}

		private static string GetFirstStackFrame(string stackTrace)
		{
			if (string.IsNullOrEmpty(stackTrace))
			{
				return "<no-stack>";
			}

			string[] frames = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			if (frames.Length == 0)
			{
				return "<no-frame>";
			}

			return frames[0].Trim();
		}

		private static void OnGodotUnhandledException(object sender, UnhandledExceptionEventArgs args)
		{
			try
			{
				Exception exception = args != null ? args.ExceptionObject as Exception : null;
				if (exception == null)
				{
					string nonExceptionText = args == null ? "null args" : Convert.ToString(args.ExceptionObject);
					if (string.IsNullOrEmpty(nonExceptionText))
					{
						nonExceptionText = "null";
					}

					exception = new Exception("Godot ExceptionUtils non-Exception object: " + nonExceptionText);
				}

				bool isTerminating = args != null && args.IsTerminating;
				DebugError("[WAIT] ExceptionUtils unhandled exception received. isTerminating=" + isTerminating + ", exceptionType=" + exception.GetType().FullName);
				ReportManagedException(exception, isTerminating, "Godot.ExceptionUtils.UnhandledException");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] OnGodotUnhandledException callback failed: " + ex);
			}
		}

		private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
		{
			try
			{
				Exception exception = args.ExceptionObject as Exception;
				if (exception == null)
				{
					string nonExceptionText = Convert.ToString(args.ExceptionObject);
					if (string.IsNullOrEmpty(nonExceptionText))
					{
						nonExceptionText = "null";
					}

					exception = new Exception("Unhandled non-Exception object: " + nonExceptionText);
				}

				DebugError("[WAIT] AppDomain unhandled exception received. isTerminating=" + args.IsTerminating + ", exceptionType=" + exception.GetType().FullName);
				ReportManagedException(exception, args.IsTerminating, "AppDomain.UnhandledException");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] OnUnhandledException callback failed: " + ex);
			}
		}

		private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
		{
			try
			{
				Exception exception = args.Exception;
				if (exception == null)
				{
					exception = new Exception("TaskScheduler.UnobservedTaskException with null exception");
				}

				DebugError("[WAIT] Unobserved task exception received. exceptionType=" + exception.GetType().FullName);
				ReportManagedException(exception, false, "TaskScheduler.UnobservedTaskException");
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] OnUnobservedTaskException callback failed: " + ex);
			}
		}

		private static void ReportManagedException(Exception exception, bool isTerminating, string source)
		{
			if (Volatile.Read(ref _unloading) == 1)
			{
				return;
			}

			DebugLog("[WAIT] ReportManagedException source=" + source + ", isTerminating=" + isTerminating);

			PendingManagedReport report = BuildPendingManagedReport(exception, isTerminating, source);
			if (isTerminating && IsGodotMainThread())
			{
				SubmitManagedReportOnMainThread(report);
				return;
			}

			EnqueueManagedReport(report);
			RequestDrainOnMainThread();

			if (IsGodotMainThread())
			{
				DrainPendingManagedReports();
			}
		}

		private static PendingManagedReport BuildPendingManagedReport(Exception exception, bool isTerminating, string source)
		{
			string exceptionType = exception.GetType().FullName;
			if (string.IsNullOrEmpty(exceptionType))
			{
				exceptionType = exception.GetType().Name;
			}

			return new PendingManagedReport
			{
				Message = source + ": " + exceptionType + ": " + exception.Message,
				StackTrace = exception.ToString(),
				Source = source,
				IsTerminating = isTerminating
			};
		}

		private static void EnqueueManagedReport(PendingManagedReport report)
		{
			if (Volatile.Read(ref _unloading) == 1)
			{
				return;
			}

			while (_pendingManagedReports.Count >= MaxPendingManagedReports)
			{
				if (!_pendingManagedReports.TryDequeue(out PendingManagedReport droppedReport))
				{
					break;
				}

				DebugError("[FAIL] Dropping queued managed exception report because the queue is full. source=" + droppedReport.Source);
			}

			_pendingManagedReports.Enqueue(report);
			DebugLog("[OK] Queued managed exception report. source=" + report.Source);
		}

		internal static void DrainPendingManagedReports()
		{
			if (Volatile.Read(ref _unloading) == 1)
			{
				return;
			}

			if (!IsGodotMainThread())
			{
				return;
			}

			while (_pendingManagedReports.TryDequeue(out PendingManagedReport report))
			{
				if (SubmitManagedReportOnMainThread(report))
				{
					continue;
				}

				report.Attempts++;
				if (report.Attempts < MaxManagedReportAttempts)
				{
					_pendingManagedReports.Enqueue(report);
				}
				else
				{
					DebugError("[FAIL] Dropping managed exception report after retry limit. source=" + report.Source);
				}

				return;
			}
		}

		private static bool SubmitManagedReportOnMainThread(PendingManagedReport report)
		{
			if (!TryGetBugQuestNode(out Node bugQuestNode))
			{
				DebugError("[FAIL] BugQuest node not found at path " + BugQuestNodePath);
				return false;
			}

			if (report.IsTerminating)
			{
				try
				{
					// Best-effort immediate submission before process termination.
					bugQuestNode.Call("report_managed_exception", report.Message, report.StackTrace, true);
					DebugLog("[OK] Immediate managed exception report submitted");
					return true;
				}
				catch (Exception ex)
				{
					DebugError("[FAIL] Immediate managed exception report failed: " + ex.Message);
					return false;
				}
			}

			try
			{
				bugQuestNode.CallDeferred("report_managed_exception", report.Message, report.StackTrace, false);
				DebugLog("[OK] Deferred managed exception report scheduled");
				return true;
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Deferred managed exception report failed: " + ex.Message);
				return false;
			}
		}

		internal static void MarkGodotMainThread()
		{
			Interlocked.Exchange(ref _mainThreadId, System.Environment.CurrentManagedThreadId);
			CaptureGodotSynchronizationContext();
		}

		private static void CaptureGodotSynchronizationContext()
		{
			SynchronizationContext currentContext = SynchronizationContext.Current;
			if (currentContext != null)
			{
				_godotSynchronizationContext = currentContext;
			}
		}

		private static bool IsGodotMainThread()
		{
			return Volatile.Read(ref _mainThreadId) == System.Environment.CurrentManagedThreadId;
		}

		private static void RequestDrainOnMainThread()
		{
			if (Volatile.Read(ref _unloading) == 1)
			{
				return;
			}

			SynchronizationContext context = _godotSynchronizationContext;
			if (context == null)
			{
				return;
			}

			try
			{
				context.Post(_ =>
				{
					MarkGodotMainThread();
					DrainPendingManagedReports();
				}, null);
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] Failed to request managed exception drain: " + ex.Message);
			}
		}

		private static void ClearPendingManagedReports()
		{
			while (_pendingManagedReports.TryDequeue(out PendingManagedReport ignoredReport))
			{
			}
		}

		private static bool TryGetBugQuestNode(out Node bugQuestNode)
		{
			bugQuestNode = null;

			try
			{
				SceneTree sceneTree = Engine.GetMainLoop() as SceneTree;
				if (sceneTree == null)
				{
					DebugError("[FAIL] Engine main loop is not a SceneTree");
					return false;
				}

				Node root = sceneTree.Root;
				if (root == null || !root.IsInsideTree())
				{
					DebugError("[FAIL] SceneTree root is unavailable");
					return false;
				}

				Node node = root.GetNodeOrNull<Node>("BugQuest");
				if (node == null || !GodotObject.IsInstanceValid(node))
				{
					DebugError("[FAIL] Node lookup failed for " + BugQuestNodePath);
					return false;
				}

				bugQuestNode = node;
				DebugLog("[OK] Located BugQuest node");
				return true;
			}
			catch (Exception ex)
			{
				DebugError("[FAIL] TryGetBugQuestNode exception: " + ex.Message);
				return false;
			}
		}

		private static void DebugLog(string message)
		{
			//GD.Print(LogPrefix + "[T" + System.Environment.CurrentManagedThreadId + "] " + message);
		}

		private static void DebugError(string message)
		{
			//GD.PrintErr(LogPrefix + "[T" + System.Environment.CurrentManagedThreadId + "] " + message);
		}
	}
}
