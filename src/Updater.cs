using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DoubleCtrl
{
    // 自动更新：查 GitHub 最新 Release，比当前版本新就下载 DoubleCtrl.exe，
    // 等用户没在用翻译窗口时替换自身并重启。运行中的 exe 不能覆盖但可以改名，
    // 所以先把自己改名成 .old，新文件放到原位置，新进程启动后删掉 .old。
    internal static class Updater
    {
        public const string UpdatedArgument = "--updated";
        private const string LatestUrl = "https://api.github.com/repos/Jawon-95/double-ctrl/releases/latest";
        private const string AssetName = "DoubleCtrl.exe";

        public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;

        public static string CurrentText
        {
            get { return Current.ToString(3); }
        }

        private static string ExePath
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        private static string OldPath
        {
            get { return ExePath + ".old"; }
        }

        // 上次更新留下的旧文件，新进程起来时旧进程可能还没完全退出，删不掉就等下次。
        public static void CleanUp()
        {
            try
            {
                if (File.Exists(OldPath)) File.Delete(OldPath);
            }
            catch (Exception e) { Log.Write("update cleanup: " + e.Message); }
        }

        // 有新版本就下载好并校验，返回下载的文件路径；没有新版本或失败返回 null。
        public static async Task<string> DownloadAsync()
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                // GitHub API 要求带 User-Agent。
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DoubleCtrl/" + CurrentText);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string json = await http.GetStringAsync(LatestUrl).ConfigureAwait(false);
                var release = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);

                Version latest;
                string tag = release["tag_name"] as string ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out latest)) return null;
                latest = Normalize(latest);
                Log.Write("update: current " + Current + ", latest " + latest);
                if (latest <= Current) return null;

                var asset = ((object[])release["assets"]).Cast<Dictionary<string, object>>()
                    .FirstOrDefault(a => (a["name"] as string) == AssetName);
                if (asset == null) return null;
                string url = (string)asset["browser_download_url"];
                long size = Convert.ToInt64(asset["size"]);

                string dir = Path.Combine(Paths.Data, "update");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "DoubleCtrl-" + latest.ToString(3) + ".exe");
                byte[] data = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (data.Length != size) throw new InvalidDataException("size " + data.Length + " != " + size);
                File.WriteAllBytes(file, data);

                // 文件里的版本号要和 Release 的版本一致，防止下到不完整或不对的文件。
                Version fileVersion;
                string fileVersionText = FileVersionInfo.GetVersionInfo(file).FileVersion ?? "";
                if (!Version.TryParse(fileVersionText, out fileVersion) || Normalize(fileVersion) != latest)
                {
                    File.Delete(file);
                    throw new InvalidDataException("file version " + fileVersionText + " != " + latest);
                }
                return file;
            }
        }

        // 换上新文件并启动新进程，成功后调用方退出当前进程。
        public static bool Install(string file)
        {
            string exe = ExePath, old = OldPath;
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Copy(file, exe); }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
                Process.Start(exe, UpdatedArgument);
                File.Delete(file);
                return true;
            }
            catch (Exception e)
            {
                // 常见原因是 exe 放在没有写权限的目录，比如 Program Files。
                Log.Crash("update install failed: " + e);
                return false;
            }
        }

        // 1.2 和 1.2.0.0 算同一个版本。
        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        }
    }
}
