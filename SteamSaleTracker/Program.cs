
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace SteamSaleTracker
{
    class Program
    {
        private static readonly HttpClient Client = new HttpClient();

        private const int PageSize = 50;

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            RunAsync().GetAwaiter().GetResult();

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static async Task RunAsync()
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("                  STEAM SALE TRACKER");
            Console.WriteLine("==========================================================");
            Console.WriteLine("Region       : India");
            Console.WriteLine("Currency     : INR");
            Console.WriteLine("Authentication: Not required");
            Console.WriteLine("==========================================================");
            Console.WriteLine();

            Client.Timeout = TimeSpan.FromSeconds(60);

            Client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");

            Client.DefaultRequestHeaders.Referrer =
                new Uri("https://store.steampowered.com/");

            List<SteamGame> games = new List<SteamGame>();

            HashSet<int> processedIds = new HashSet<int>();

            int start = 0;
            int totalCount = 0;
            int page = 0;

            try
            {
                while (true)
                {
                    page++;

                    Console.WriteLine(
                        "Fetching page {0} | Start index: {1}",
                        page, start);

                    string url =
                        "https://store.steampowered.com/search/results/?" +
                        "query=&" +
                        "start=" + start +
                        "&count=" + PageSize +
                        "&dynamic_data=" +
                        "&sort_by=Price_ASC" +
                        "&specials=1" +
                        "&supportedlang=english" +
                        "&infinite=1" +
                        "&cc=IN" +
                        "&l=english";

                    using (HttpResponseMessage response =
                        await Client.GetAsync(url))
                    {
                        string json =
                            await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine(
                                "Steam request failed: {0}",
                                response.StatusCode);

                            Console.WriteLine(json);
                            break;
                        }

                        JObject root = JObject.Parse(json);

                        int success = (int?)root["success"] ?? 0;

                        if (success != 1)
                        {
                            Console.WriteLine(
                                "Steam returned an unsuccessful response.");

                            break;
                        }

                        totalCount = (int?)root["total_count"] ?? 0;

                        string html =
                            (string)root["results_html"];

                        if (string.IsNullOrWhiteSpace(html))
                        {
                            Console.WriteLine(
                                "No results returned by Steam.");

                            break;
                        }

                        HtmlDocument document = new HtmlDocument();
                        document.LoadHtml(html);

                        HtmlNodeCollection rows =
                            document.DocumentNode.SelectNodes(
                                "//a[contains(@class,'search_result_row')]");

                        if (rows == null || rows.Count == 0)
                        {
                            Console.WriteLine(
                                "No more game results found.");

                            break;
                        }

                        int pageGames = 0;

                        foreach (HtmlNode row in rows)
                        {
                            SteamGame game = ParseGame(row);

                            if (game == null)
                                continue;

                            if (!processedIds.Add(game.AppId))
                                continue;

                            games.Add(game);
                            pageGames++;
                        }

                        Console.WriteLine(
                            "Games found on page: {0}",
                            pageGames);

                        Console.WriteLine(
                            "Total available results: {0}",
                            totalCount);

                        Console.WriteLine(
                            "Total unique games fetched: {0}",
                            games.Count);

                        if (pageGames == 0)
                        {
                            Console.WriteLine(
                                "No new games found. Stopping pagination.");

                            break;
                        }

                        start += PageSize;

                        if (start >= totalCount)
                            break;
                    }

                    // Avoid sending requests too quickly.
                    await Task.Delay(1000);
                }

                Console.WriteLine();
                Console.WriteLine("==========================================================");
                Console.WriteLine("                 FETCH COMPLETED");
                Console.WriteLine("==========================================================");

                DisplayGames(games);

            }
            catch (TaskCanceledException)
            {
                Console.WriteLine(
                    "Request timed out or was cancelled.");
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine(
                    "Network error: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "Unexpected error: " + ex.Message);

                Console.WriteLine(ex.StackTrace);
            }
        }

        private static SteamGame ParseGame(HtmlNode row)
        {
            try
            {
                string appIdValue =
                    row.GetAttributeValue("data-ds-appid", "");

                if (string.IsNullOrWhiteSpace(appIdValue))
                    return null;

                Match appIdMatch = Regex.Match(
                    appIdValue, @"\d+");

                if (!appIdMatch.Success)
                    return null;

                int appId;

                if (!int.TryParse(appIdMatch.Value, out appId))
                    return null;

                string name = HtmlEntity.DeEntitize(
                    row.SelectSingleNode(
                        ".//*[contains(@class,'title')]")?.InnerText ?? "");

                name = name.Trim();

                if (string.IsNullOrWhiteSpace(name))
                    return null;

                string discountText =
                    row.SelectSingleNode(
                        ".//*[contains(@class,'discount_pct')]")?.InnerText;

                int discount = 0;

                if (!string.IsNullOrWhiteSpace(discountText))
                {
                    Match discountMatch = Regex.Match(
                        discountText, @"\d+");

                    if (discountMatch.Success)
                    {
                        int.TryParse(
                            discountMatch.Value, out discount);
                    }
                }

                string originalText =
                    row.SelectSingleNode(
                        ".//*[contains(@class,'discount_original_price')]")
                    ?.InnerText;

                string finalText =
                    row.SelectSingleNode(
                        ".//*[contains(@class,'discount_final_price')]")
                    ?.InnerText;

                decimal originalPrice;
                decimal currentPrice;

                if (!TryParsePrice(originalText, out originalPrice))
                    return null;

                if (!TryParsePrice(finalText, out currentPrice))
                    return null;

                if (discount <= 0 || currentPrice >= originalPrice)
                    return null;

                return new SteamGame
                {
                    AppId = appId,
                    Name = name,
                    OriginalPrice = originalPrice,
                    CurrentPrice = currentPrice,
                    Discount = discount,
                    StoreUrl =
                        "https://store.steampowered.com/app/" +
                        appId + "/?cc=IN"
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParsePrice(
            string text, out decimal price)
        {
            price = 0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = HtmlEntity.DeEntitize(text);

            // Remove currency symbols and retain the numeric price.
            Match match = Regex.Match(
                text, @"[\d,]+(?:\.\d{1,2})?");

            if (!match.Success)
                return false;

            string numericValue =
                match.Value.Replace(",", "");

            return decimal.TryParse(
                numericValue,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out price);
        }

        private static void DisplayGames(
            List<SteamGame> games)
        {
            if (games == null || games.Count == 0)
            {
                Console.WriteLine(
                    "No discounted games were retrieved.");

                return;
            }

            var sortedGames = games
                .OrderByDescending(x => x.Discount)
                .ThenBy(x => x.CurrentPrice)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("Total discounted games: " +
                sortedGames.Count);

            Console.WriteLine();

            Console.WriteLine(
                "{0,-38} {1,12} {2,12} {3,9}",
                "GAME NAME",
                "ORIGINAL",
                "CURRENT",
                "OFF");

            Console.WriteLine(new string('-', 76));

            foreach (SteamGame game in sortedGames)
            {
                string name = game.Name.Length > 36
                    ? game.Name.Substring(0, 33) + "..."
                    : game.Name;

                Console.WriteLine(
                    "{0,-38} {1,12} {2,12} {3,8}%",
                    name,
                    FormatPrice(game.OriginalPrice),
                    FormatPrice(game.CurrentPrice),
                    game.Discount);
            }

            Console.WriteLine(new string('-', 76));

            Console.WriteLine();
            Console.WriteLine("DETAILED GAME INFORMATION");
            Console.WriteLine(new string('=', 76));

            foreach (SteamGame game in sortedGames)
            {
                Console.WriteLine();
                Console.WriteLine("Game       : " + game.Name);
                Console.WriteLine("App ID     : " + game.AppId);
                Console.WriteLine("Original   : " +
                    FormatPrice(game.OriginalPrice));
                Console.WriteLine("Current    : " +
                    FormatPrice(game.CurrentPrice));
                Console.WriteLine("Discount   : " +
                    game.Discount + "%");

                Console.WriteLine(
                    "You Save   : " +
                    FormatPrice(
                        game.OriginalPrice - game.CurrentPrice));

                Console.WriteLine("Steam URL  : " + game.StoreUrl);

                Console.WriteLine(
                    new string('-', 76));
            }

            Console.WriteLine();
            Console.WriteLine("Source: Official Steam Store");
            Console.WriteLine("Region: India");
            Console.WriteLine("Games displayed: " + sortedGames.Count);
        }

        private static string FormatPrice(decimal price)
        {
            return "Rs. " + price.ToString(
                "N2",
                CultureInfo.GetCultureInfo("en-IN"));
        }
    }

    public class SteamGame
    {
        public int AppId { get; set; }

        public string Name { get; set; }

        public decimal OriginalPrice { get; set; }

        public decimal CurrentPrice { get; set; }

        public int Discount { get; set; }

        public string StoreUrl { get; set; }
    }
}
