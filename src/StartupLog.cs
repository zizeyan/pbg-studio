// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Text;

namespace PbgStudio
{
    // Logging must never be the reason startup fails. No project contents are logged.
    internal static class StartupLog
    {
        static readonly object Sync = new object();
        static string path;
        internal static string PathName { get { return path ?? ""; } }

        internal static void Initialize(string baseDirectory)
        {
            string[] folders = { Path.Combine(baseDirectory, "data"), Path.Combine(Path.GetTempPath(), "PbgStudio") };
            lock (Sync)
            {
                path = null;
                foreach (string folder in folders)
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        string candidate = Path.Combine(folder, "startup.log");
                        File.WriteAllText(candidate, "PBG Studio startup diagnostic\r\n", new UTF8Encoding(false));
                        path = candidate;
                        break;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
            Write("ENTRY", "Version=" + typeof(StartupLog).Assembly.GetName().Version + "; OS=" + Environment.OSVersion + "; CLR=" + Environment.Version + "; 64bitProcess=" + Environment.Is64BitProcess + "; 64bitOS=" + Environment.Is64BitOperatingSystem);
        }

        internal static void Write(string stage, string detail)
        {
            lock (Sync)
            {
                if (path == null) return;
                try { File.AppendAllText(path, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " [" + stage + "] " + detail + "\r\n", new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }

        internal static int Run(string baseDirectory, Func<int> start, Action<Exception, string> notify)
        {
            try
            {
                Initialize(baseDirectory);
                return start();
            }
            catch (Exception ex)
            {
                Write("STARTUP FAILED", ex.ToString());
                try { Console.Error.WriteLine(ex.ToString()); } catch { }
                if (notify != null) { try { notify(ex, PathName); } catch { } }
                return 1;
            }
        }
    }
}
