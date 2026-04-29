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
    }
}
