using System;
using CenterCLR.Sgml;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CsvHelper;

namespace PSVRWebScraper
{
    public class Program
    {
        class GameInformation
        {
            public string title { get; set; }
            public string vendor { get; set; }
            public string psvr { get; set; }
            public string release_date { get; set; }
            public string genre { get; set; }
            public string price_package { get; set; }
            public string price_download { get; set; }
            public string cero_rating { get; set; }
            public string support_move { get; set; }
            public string extra_info { get; set; }
        }

        Dictionary<string, string> ParseDescriptionStage1(string desc)
        {
            var dict = new Dictionary<string, string>();
            string lastKey = null;
            var regExp = new Regex("^([^：]+)：(.*)$");

            var isFirstLine = true;
            foreach (var line in desc.Split('\n'))
            {
                if (isFirstLine)
                {
                    // first line contains vendor information
                    lastKey = "vendor";
                    dict[lastKey] = line;
                    isFirstLine = false;
                    continue;
                }
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                if (line.Contains("："))
                {
                    var matches = regExp.Matches(line);
                    if (matches.Count == 0)
                    {
                        Console.WriteLine("something went wrong.");
                        break;
                    }
                    var m = matches[0].Groups;
                    lastKey = m[1].Value;
                    dict[lastKey] = m[2].Value;
                }
                else if (line == "2016年冬発売予定" || line == "発売中　※PS VRにはアップデートで対応予定")
                {
                    // 🐻 or 39 (corrupt data)
                    lastKey = "発売予定日";
                    dict[lastKey] = line;
                }
                else if (!string.IsNullOrEmpty(lastKey))
                {
                    if (string.IsNullOrEmpty(dict[lastKey]))
                    {
                        dict[lastKey] = line;
                    }
                    else
                    {
                        dict[lastKey] += "\n" + line;
                    }
                }
            }
            return dict;
        }

        GameInformation ParseDescriptionStage2(string title, Dictionary<string, string> dict)
        {
            var inf = new GameInformation
            {
                title = title,
                vendor = dict["vendor"],
                psvr = dict["PS VR"],
                genre = dict["ジャンル"],
                support_move = dict["PS Move"]
            };
            dict.Remove("title");
            dict.Remove("vendor");
            dict.Remove("PS VR");
            dict.Remove("ジャンル");
            dict.Remove("PS Move");
            if (dict.ContainsKey("発売予定日"))
            {
                inf.release_date = dict["発売予定日"];
                dict.Remove("発売予定日");
            }
            else if (dict.ContainsKey("好評発売中"))
            {
                inf.release_date = "発売中 " + dict["好評発売中"];
                dict.Remove("好評発売中");
            }

            if (dict.ContainsKey("ダウンロード版"))
            {
                inf.price_download = dict["ダウンロード版"];
                dict.Remove("ダウンロード版");
            }
            if (dict.ContainsKey("パッケージ版"))
            {
                inf.price_package = dict["パッケージ版"];
                dict.Remove("パッケージ版");
            }

            if (dict.ContainsKey("CERO"))
            {
                inf.cero_rating = dict["CERO"];
                dict.Remove("CERO");
            }
            var ceroRating = dict.Keys.Where(k => k.StartsWith("CERO ")).FirstOrDefault();
            if (ceroRating != null)
            {
                inf.cero_rating = $"{ceroRating}: ({dict[ceroRating]})";
                dict.Remove(ceroRating);
            }
            var extraInfo = string.Join("\n", dict.Select(kv => $"{kv.Key}: {kv.Value}"));
            if (!string.IsNullOrEmpty(extraInfo))
            {
                inf.extra_info = extraInfo;
            }
            return inf;
        }

        List<GameInformation> AnalyzeDoc(XDocument doc)
        {
            var games = new List<GameInformation>();
            var ns = doc.Root.Name.Namespace;
            var list = doc.Descendants(ns + "div").Where(o => o.Attribute("class")?.Value == "parsys styleParsys");
            // found container nodes
            if (list.Count() != 0) {
                foreach (var el in list)
                {
                    if (el.Parent.Name == "div" && el.Parent.Attribute("class")?.Value == "box960")
                    {
                        // skip demo disc
                        continue;
                    }
                    var h4El = el.Descendants(ns + "h4").FirstOrDefault();
                    var pEl = el.Descendants(ns + "div")
                        .Where(o => o.Attribute("class")?.Value == "col-lg-8 col-md-8 col-sm-12 col-xs-12")
                        .FirstOrDefault()?.Descendants(ns + "p").FirstOrDefault();
                    //Console.WriteLine(h4El.Value.Trim());
                    if (pEl != null)
                    {
                        var dict = ParseDescriptionStage1(pEl.Value);
                        games.Add(ParseDescriptionStage2(h4El != null ? h4El.Value.Trim() : "no title", dict));
                    }
                }
            }
            return games;
        }

        void DumpGameList(List<GameInformation> games)
        {
            Console.WriteLine("game count: " + games.Count);
            var filename = "games_" + DateTime.Now.ToString("yyMMdd-HHmmss") + ".csv";
            using (var fs = File.Open(filename, FileMode.Create))
            using (var writer = new StreamWriter(fs))
            {
                var helper = new CsvWriter(writer);
                helper.WriteHeader(typeof(GameInformation));
                foreach (var gameInf in games.OrderBy(m => m.title))
                {
                    helper.WriteRecord(gameInf);
                }
            }
        }

        void DumpGameListByHtmlStream(Stream s)
        {
            var doc = SgmlReader.Parse(s);
            var games = AnalyzeDoc(doc);
            DumpGameList(games);
        }

        public async Task Proc(bool online = true)
        {
            if (online)
            {
                var client = new HttpClient();
                var result = await client.GetAsync("http://www.jp.playstation.com/psvr/contents/");
                if (result.StatusCode == System.Net.HttpStatusCode.OK)
                    using (var s = await result.Content.ReadAsStreamAsync())
                    {
                        // var content = await result.Content.ReadAsStringAsync();
                        // System.IO.File.WriteAllText("samplefile.html", content);
                        DumpGameListByHtmlStream(s);
                    }
            }
            else
            {
                using (var s = new MemoryStream(System.IO.File.ReadAllBytes("samplefile.html")))
                {
                    DumpGameListByHtmlStream(s);
                }
            }
        }

        public static void Main(string[] args)
        {
            (new Program()).Proc(online: true).Wait();
        }
    }
}
