using Godot;

namespace BugQuest
{
    public static class BugQuestAPI
    {
        private const string BugQuestNodePath = "/root/BugQuest";

        private static Node GetActiveNode()
        {
            SceneTree sceneTree = Engine.GetMainLoop() as SceneTree;
            Node bq = sceneTree?.Root?.GetNodeOrNull<Node>(BugQuestNodePath);
            return bq != null && !(bool)bq.Call("is_disabled") ? bq : null;
        }

        /// <summary>Disables BugQuest collection and upload for the current app session.</summary>
        public static void Disable()
        {
            GetActiveNode()?.Call("disable");
        }

        /// <summary>Starts a replay, subject to video availability and the server duration limit.</summary>
        public static bool StartReplay(string replayLabel, bool discardReplayOnMaxDurationTimeout = false, int maxReplayDurationMinutes = int.MaxValue)
        {
            Node bq = GetActiveNode();
            return bq != null && (bool)bq.Call("start_replay", replayLabel ?? "", discardReplayOnMaxDurationTimeout, maxReplayDurationMinutes);
        }

        /// <summary>Requests replay completion, optionally discarding, relabeling, or trimming it.</summary>
        public static bool StopReplay(bool discardReplay = false, string replaceLabel = null, int onlyKeepLastMinutes = int.MaxValue)
        {
            Node bq = GetActiveNode();
            Variant label = replaceLabel == null ? default(Variant) : Variant.From(replaceLabel);
            return bq != null && (bool)bq.Call("stop_replay", discardReplay, label, onlyKeepLastMinutes);
        }

        /// <summary>Returns whether a replay is active, including pending finalization.</summary>
        public static bool IsReplayRecording()
        {
            Node bq = GetActiveNode();
            return bq != null && (bool)bq.Call("is_replay_recording");
        }

        /// <summary>Allows automatic error clips when enabled by the server configuration.</summary>
        public static void EnableErrorClips()
        {
            GetActiveNode()?.Call("enable_error_clips");
        }

        /// <summary>Suppresses error clips without stopping replays for this app session.</summary>
        public static void DisableErrorClips()
        {
            GetActiveNode()?.Call("disable_error_clips");
        }

        /// <summary>Stops and disables all video features for the rest of this app session.</summary>
        public static void DisableAllVideoFeatures()
        {
            GetActiveNode()?.Call("disable_all_video_features");
        }

        // Sends a user-submitted report (player feedback / bug report). The text is stored in the
        // report message field; optional attachment files (e.g. a screenshot) are uploaded with it.
        // The game is responsible for the UI that collects the text.
        public static bool SendUserReport(string text, string[] attachmentPaths = null)
        {
            Node bq = GetActiveNode();
            return bq != null && (bool)bq.Call("send_user_report", text, attachmentPaths ?? new string[0]);
        }
    }
}
