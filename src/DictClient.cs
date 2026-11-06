using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DoubleCtrl
{
    internal sealed class Phonetic
    {
        public string Label, Text, Speech;
    }

    internal sealed class Entry
    {
        public string Pos, Text, Detail, Speech;
        public bool IsWord; // 中译外的结果词，加粗显示并带发音
    }

    internal sealed class DictResult
    {
        public string Query, Le, Speech;
        public string Sentence, SentenceSpeech;
        public readonly List<Phonetic> Phonetics = new List<Phonetic>();
        public readonly List<Entry> Entries = new List<Entry>();
        public readonly List<string> Web = new List<string>();
        public readonly List<string> Forms = new List<string>();

        public bool IsEmpty
        {
            get { return Sentence == null && Entries.Count == 0 && Web.Count == 0; }
        }
    }

    internal static class DictClient
    {
        public const int MaxQueryLength = 5000;
        private const string ApiUrl = "https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4";
        private const string VoiceUrl = "https://dict.youdao.com/dictvoice?audio=";
        private const string Dicts = "{\"count\":99,\"dicts\":[[\"ec\",\"ce\",\"newcj\",\"newjc\",\"kc\",\"ck\",\"fc\",\"cf\",\"multle\"],[\"web_trans\"],[\"fanyi\"]]}";
        private const int MaxWeb = 5;

        private static readonly HttpClient Http = CreateClient();
        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DoubleCtrl/1.0");
            return client;
        }

        // 首次请求要走代理探测、TLS 握手和 JIT，提前做掉，第一次取词才不会卡。
        public static void WarmUp()
        {
            QueryAsync("hello", "en").ContinueWith(t => Log.Write("warmup: " + (t.Exception == null ? "ok" : t.Exception.InnerException.Message)));
        }

        public static async Task<DictResult> QueryAsync(string text, string selectedLe)
        {
            string le = PickLe(text, selectedLe);
            var form = new StringBuilder();
            form.Append("q=").Append(Uri.EscapeDataString(text));
            form.Append("&le=").Append(le);
            form.Append("&t=2&client=web&keyfrom=webdict&dicts=").Append(Uri.EscapeDataString(Dicts));
            var content = new StringContent(form.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");
            var response = await Http.PostAsync(ApiUrl, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var result = Parse(text, le, serializer.DeserializeObject(json));
            // 接口改版时最先出现的症状就是解析不出内容，留下原始响应方便排查。
            if (result.IsEmpty) Log.Write("empty result, raw: " + json.Substring(0, Math.Min(json.Length, 800)));
            return result;
        }

        public static Task<byte[]> DownloadVoiceAsync(string speech)
        {
            return Http.GetByteArrayAsync(VoiceUrl + speech);
        }

        // le 和文本语种不匹配时接口会返回乱码（比如 le=en 查日文），能从字符判断的语言优先按字符选。
        public static string PickLe(string text, string selectedLe)
        {
            foreach (char c in text)
            {
                if ((c >= '぀' && c <= 'ヿ')) return "ja";
                if ((c >= '가' && c <= '힣')) return "ko";
                if ((c >= 'Ѐ' && c <= 'ӿ')) return "ru";
            }
            return selectedLe;
        }

        public static DictResult Parse(string query, string le, object root)
        {
            var result = new DictResult { Query = query, Le = le };
            var simple = First(Get(root, "simple", "word"));
            result.Speech = Str(simple, "speech");

            ParseEc(result, Get(root, "ec"));
            ParseCe(result, Get(root, "ce"));
            foreach (var key in new[] { "fc", "cf", "kc", "ck", "multle" })
                ParseGeneric(result, Get(root, key));
            ParseJapanese(result, Get(root, "newjc"));
            ParseJapanese(result, Get(root, "newcj"));

            if (result.Phonetics.Count == 0)
            {
                string phone = Str(simple, "phone");
                if (!string.IsNullOrEmpty(phone))
                    result.Phonetics.Add(new Phonetic { Text = phone, Speech = result.Speech });
            }

            foreach (var item in Items(Get(First(Get(root, "web_trans", "web-translation")), "trans")).Take(MaxWeb))
            {
                string value = Clean(Str(item, "value"));
                if (value.Length > 0) result.Web.Add(value);
            }

            var fanyi = Get(root, "fanyi");
            if (fanyi != null)
            {
                result.Sentence = Clean(Str(fanyi, "tran"));
                result.SentenceSpeech = Str(fanyi, "voice");
            }
            return result;
        }

        private static void ParseEc(DictResult result, object dict)
        {
            var word = First(Get(dict, "word"));
            if (word == null) return;
            AddPhonetic(result, "英", Str(word, "ukphone"), Str(word, "ukspeech"));
            AddPhonetic(result, "美", Str(word, "usphone"), Str(word, "usspeech"));
            foreach (var tr in Items(Get(word, "trs")))
            {
                string text = Clean(Str(tr, "tran"));
                if (text.Length == 0) text = JoinTr(tr);
                if (text.Length > 0) result.Entries.Add(new Entry { Pos = Str(tr, "pos"), Text = text });
            }
            foreach (var item in Items(Get(word, "wfs")))
            {
                var wf = Get(item, "wf");
                result.Forms.Add(Str(wf, "name") + " " + Str(wf, "value"));
            }
        }

        private static void ParseCe(DictResult result, object dict)
        {
            var word = First(Get(dict, "word"));
            if (word == null) return;
            foreach (var tr in Items(Get(word, "trs")))
            {
                string text = Clean(Str(tr, "#text"));
                if (text.Length > 0)
                    result.Entries.Add(new Entry { Text = text, Detail = Clean(Str(tr, "#tran")), Speech = Str(tr, "voice"), IsWord = true });
            }
        }

        private static void ParseGeneric(DictResult result, object dict)
        {
            foreach (var word in Items(Get(dict, "word")))
            {
                foreach (var tr in Items(Get(word, "trs")))
                {
                    string text = JoinTr(tr);
                    if (text.Length > 0) result.Entries.Add(new Entry { Pos = Str(tr, "pos"), Text = text });
                }
            }
        }

        private static void ParseJapanese(DictResult result, object dict)
        {
            var word = Get(dict, "word");
            if (word == null) return;
            var head = Get(word, "head");
            string reading = Str(head, "sound");
            if (reading.Length == 0)
                reading = (Str(head, "pjm") + " " + Str(head, "tone") + " " + Str(head, "rs")).Trim();
            if (reading.Length > 0)
                result.Phonetics.Add(new Phonetic { Text = reading, Speech = result.Speech });
            foreach (var sense in Items(Get(word, "sense")))
            {
                var meanings = Items(Get(sense, "phrList")).Select(p => Clean(Str(p, "jmsy"))).Where(s => s.Length > 0).ToList();
                if (meanings.Count > 0)
                    result.Entries.Add(new Entry { Pos = Str(sense, "cx"), Text = string.Join("；", meanings) });
            }
        }

        private static void AddPhonetic(DictResult result, string label, string text, string speech)
        {
            if (!string.IsNullOrEmpty(text))
                result.Phonetics.Add(new Phonetic { Label = label, Text = text, Speech = speech });
        }

        private static string JoinTr(object tr)
        {
            var parts = new List<string>();
            foreach (var item in Items(Get(tr, "tr")))
            {
                var inner = Get(item, "l", "i");
                if (inner is string) parts.Add(Clean((string)inner));
                else foreach (var line in Items(inner))
                    if (line is string) parts.Add(Clean((string)line));
            }
            return string.Join("；", parts.Where(p => p.Length > 0));
        }

        private static string Clean(string text)
        {
            return text == null ? "" : WebUtility.HtmlDecode(Tags.Replace(text, "")).Trim();
        }

        private static object Get(object node, params string[] path)
        {
            foreach (var key in path)
            {
                var map = node as IDictionary<string, object>;
                if (map == null || !map.TryGetValue(key, out node)) return null;
            }
            return node;
        }

        private static string Str(object node, string key)
        {
            var value = Get(node, key);
            return value == null ? "" : Convert.ToString(value);
        }

        // 接口里同一字段有时是对象有时是数组，统一成列表处理。
        private static IEnumerable<object> Items(object node)
        {
            if (node == null) return new object[0];
            var list = node as IEnumerable;
            if (list != null && !(node is string) && !(node is IDictionary<string, object>))
                return list.Cast<object>();
            return new[] { node };
        }

        private static object First(object node)
        {
            return Items(node).FirstOrDefault();
        }
    }
}
