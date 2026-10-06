using System;
using System.IO;

namespace BoomBx
{
    /// <summary>
    /// One place for every folder the app writes to.
    /// </summary>
    public static class AppPaths
    {
        public const string AppName = "Boobies Soundpad";
        public const string AssemblyName = "BoobiesSoundpad";

        public static string DataDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AssemblyName);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string IconsDir
        {
            get
            {
                var dir = Path.Combine(DataDir, "icons");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ToolsDir
        {
            get
            {
                var dir = Path.Combine(DataDir, "tools");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public const string DefaultIcon = "avares://" + AssemblyName + "/Assets/default-sound.png";
    }
}
