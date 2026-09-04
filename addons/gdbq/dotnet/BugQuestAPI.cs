using Godot;

namespace BugQuest
{
    public static class BugQuestAPI
    {
        private const string BugQuestNodePath = "/root/BugQuest";

        public static void Disable()
        {
            SceneTree sceneTree = Engine.GetMainLoop() as SceneTree;
            if (sceneTree == null || sceneTree.Root == null)
            {
                return;
            }

            Node bq = sceneTree.Root.GetNodeOrNull<Node>(BugQuestNodePath);
            if (bq != null && !(bool)bq.Call("is_disabled"))
            {
                bq.Call("disable");
            }
        }

        // Sends a user-submitted report (player feedback / bug report). The text is stored in the
        // report message field; optional attachment files (e.g. a screenshot) are uploaded with it.
        // The game is responsible for the UI that collects the text.
        public static bool SendUserReport(string text, string[] attachmentPaths = null)
        {
            SceneTree sceneTree = Engine.GetMainLoop() as SceneTree;
            if (sceneTree == null || sceneTree.Root == null)
            {
                return false;
            }

            Node bq = sceneTree.Root.GetNodeOrNull<Node>(BugQuestNodePath);
            if (bq == null)
            {
                return false;
            }

            return (bool)bq.Call("send_user_report", text, attachmentPaths ?? new string[0]);
        }
    }
}
