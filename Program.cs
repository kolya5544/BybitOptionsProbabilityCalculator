using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.Interpolation;
using ScottPlot;
using ScottPlot.Statistics;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using Newtonsoft.Json;

namespace BybitOptionsProbabilityCalculator
{
    internal class Program
    {
        public static decimal underlyingPrice = 0m;
        public static BybitRestClient rest;

        static async Task Main(string[] args)
        {
            // init REST client
            rest = new BybitRestClient();

            string coin = Prompt("Enter the coin (ex. BTC, ETH, SOL)").ToUpper();

            var sorted = await UpdateOptionsAndTickerAsync(coin);

            while (true)
            {
                Console.Clear();
                var dates = GetListOfDates(sorted);
                Console.WriteLine("Select an expiration date:");
                for (int i = 0; i < dates.Count; i++)
                {
                    var t = dates[i].ToString("dMMMyy", CultureInfo.GetCultureInfo("en-US")).ToUpper();
                    var expiryInDays = Math.Round((dates[i] - DateTime.UtcNow).TotalDays, 1);
                    Console.WriteLine($"{i + 1}. [ {t} ], expires in approx. {expiryInDays} days.");
                }

                var date = Prompt("Enter the number of the date");
                var dateIndex = int.Parse(date) - 1;
                var dateToUse = dates[dateIndex];

                await OutputAllOptions(sorted, coin, dateToUse);
            }
        }

        public static List<DateTime> GetListOfDates(List<BybitOptionTicker> options) => options.Select(x => ParseOption(x.Symbol).Item1).Distinct().ToList();

        public static async Task OutputAllOptions(List<BybitOptionTicker> options, string coin, DateTime dateToUse)
        {
            var shouldExit = new CancellationTokenSource();

            new Thread(() =>
            {
                Console.ReadLine();
                shouldExit.Cancel();
            }).Start();

            while (true)
            {
                options = options.Where(x => ParseOption(x.Symbol).Item1 == dateToUse).ToList();

                Console.Clear();
                var p = ParseOption(options.First().Symbol);
                var date = p.Item1;
                var t = date.ToString("dMMMyy", CultureInfo.GetCultureInfo("en-US")).ToUpper();
                var expiryInDays = Math.Round((date - DateTime.UtcNow).TotalDays, 1);

                Console.WriteLine($"{coin} [ {t} ], expires in approx. {expiryInDays} days.");
                Console.WriteLine("");

                // find biggest price length
                var maxPriceLength = options.Max(x => ParseOption(x.Symbol).Item2.ToString().Length);

                bool currentPriceOut = false;
                for (int i = 0; i < options.Count / 2; i++)
                {
                    // we'll have CALL then PUT for the same option, which is why i / 2.
                    var call = options[i * 2];
                    var put = options[i * 2 + 1];

                    // if current price < strike price, then output a line to denote that...
                    var stCall = ParseOption(call.Symbol).Item2;
                    var stPut = ParseOption(call.Symbol).Item2;
                    if (!currentPriceOut && underlyingPrice < stCall)
                    {
                        currentPriceOut = true;
                        SC(ConsoleColor.White, $"C [ -----------------------------------> ]  [ ${underlyingPrice} ]  [ <{"".PadRight(39 - (underlyingPrice.ToString().Length - stCall.ToString().Length)-1, '-')} ] P\r\n");
                    }

                    // calculate P(touch) for call and put
                    var pC = Ptouch(call, underlyingPrice);
                    var pP = Ptouch(put, underlyingPrice);

                    var msg = $"C [ ";
                    SC(ConsoleColor.White, msg);
                    msg = $"P(D)% = {Math.Round(call.Delta * 100, 2)}%".PadRight(15) + " | ";
                    SC(ConsoleColor.Green, msg);
                    msg = $"P(Touch)% = {Math.Round(pC * 100, 2)}%".PadRight(18);
                    if (underlyingPrice < stCall)
                    {
                        SC(ConsoleColor.DarkGreen, msg);
                    }
                    else Console.Write("".PadRight(msg.Length));
                    msg = " ]";
                    SC(ConsoleColor.White, msg);
                    msg = $"   [ ${ParseOption(call.Symbol).Item2.ToString().PadRight(maxPriceLength)} ]   ";
                    SC(ConsoleColor.Red, msg);
                    msg = $"[ ";
                    SC(ConsoleColor.White, msg);
                    msg = $"P(D)% = {Math.Round(-put.Delta * 100, 2)}%".PadRight(15) + " | ";
                    SC(ConsoleColor.Green, msg);
                    msg = $"P(Touch)% = {Math.Round(pP * 100, 2)}%".PadRight(18);
                    if (underlyingPrice > stPut)
                    {
                        SC(ConsoleColor.DarkGreen, msg);
                    }
                    else Console.Write("".PadRight(msg.Length));
                    msg = $" ] P. IV = {Math.Round(call.MarkIv * 100, 2)}%\r\n";
                    SC(ConsoleColor.White, msg);
                    RC();
                }
                Console.WriteLine();
                Console.WriteLine("Conclusions drawn...");
                //Console.WriteLine("The most likely price ranges based on P(D)%... GENERATING CHARTS");
                var settlement = GeneratePriceRangeLikelihood(options, false);
                var touch = GeneratePriceRangeLikelihood(options, true);
                Console.WriteLine($"Settlement KDE: ${settlement.kdeEstimation} ({DiffPerc((double)underlyingPrice, settlement.kdeEstimation)}%), touch KDE: ${touch.kdeEstimation} ({DiffPerc((double)underlyingPrice, touch.kdeEstimation)}%)");

                /*
                // find extrema for KDE and print the price points out
                var significantE = FindSignificantExtrema(settlement.kdeValues);
                // map extrema to xValues
                var SEindex = significantE.Select(x => x.index).ToArray();
                Console.WriteLine($"Significant points of interest (KDE):");
                for (int i = 0; i < SEindex.Length; i++)
                {
                    var x = settlement.xValues[SEindex[i]];
                    Console.WriteLine($"${Math.Round(x)} ({DiffPerc((double)underlyingPrice, x)})");
                }
                */

                Console.WriteLine();
                Console.WriteLine("Press ENTER to return to datetime selection.");
                try
                {
                    await Task.Delay(60 * 1000, shouldExit.Token);
                }
                catch { }
                if (shouldExit.IsCancellationRequested) return;
                await UpdateOptionsAndTickerAsync(coin);
            }
        }

        public static double DiffPerc(double a, double b)
        {
            return Math.Round((b - a) / a * 100, 2);
        }

        private static (double kdeEstimation, double[] xValues, double[] kdeValues) GeneratePriceRangeLikelihood(List<BybitOptionTicker> options, bool deltaOrModel = false)
        {
            // only keep CALLS
            var opt = options.Where(t => ParseOption(t.Symbol).Item3 == 'C').ToList();

            // list of normal probabilities
            var normal_probs = new Dictionary<decimal, double>();

            opt.ForEach(ticker =>
            {
                var p = ParseOption(ticker.Symbol);

                var val = 0d;
                if (deltaOrModel)
                {
                    if (underlyingPrice <= p.Item2)
                    {
                        val = Math.Round(Ptouch(ticker, underlyingPrice) * 100, 2);
                    } else
                    {
                        var put = options.FirstOrDefault(x => {
                            var a = ParseOption(ticker.Symbol);
                            var b = ParseOption(x.Symbol);

                            return a.Item2 == b.Item2 && b.Item3 == 'P';
                        });

                        val = Math.Round(Ptouch(put, underlyingPrice) * 100, 2);
                    }
                } else
                {
                    val = (double)Math.Round(ticker.Delta * 100, 2);
                }
                normal_probs.Add(p.Item2, val);
            });

            // build KDE (Kernel Density Estimation)
            var input = normal_probs.Select((z) => ((double)z.Key, (double)z.Value)).ToArray();
            var kde = KDE(input);
            int maxKdeIndex = Array.IndexOf(kde.kdeValues, kde.kdeValues.Max());
            var kdeMLP = Math.Round(kde.xValues[maxKdeIndex]);

            // generate list of Interval Probabilities
            var intervalProbabilities = new Dictionary<string, double>();
            // calculate interval probabilities
            for (int i = 0; i < normal_probs.Count - 1; i++)
            {
                var first = normal_probs.ElementAt(i);
                var second = normal_probs.ElementAt(i + 1);

                if (underlyingPrice >= first.Key && underlyingPrice <= second.Key && deltaOrModel)
                {
                    continue; // skip it for touch probability to avoid skewing the chart
                }

                var interval = $"${first.Key}-${second.Key}";
                var rangeSize = second.Key - first.Key;
                var prob = Math.Abs(first.Value - second.Value) / (double)rangeSize;
                if (Math.Abs(first.Value - second.Value) == 0 && deltaOrModel)
                {
                    prob = 1.0d / (double)rangeSize;
                }

                intervalProbabilities.Add(interval, prob);
            }

            var plt = new Plot();

            // Extract intervals and probabilities
            Tick[] intervals = new Tick[intervalProbabilities.Count];
            double[] probabilities = new double[intervalProbabilities.Count];
            
            for (int i = 0; i < intervalProbabilities.Count; i++)
            {
                var key = intervalProbabilities.Keys.ToList()[i];
                var value = intervalProbabilities[key];

                intervals[i] = new Tick(i, key);
                probabilities[i] = value;
            }

            // Create bar chart
            var bars = plt.Add.Bars(probabilities);

            // Set X-axis labels
            plt.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(intervals);
            plt.Axes.Bottom.Label.Text = "Price Intervals ($)";
            //plt.Axes.Bottom.TickGenerator = intervals;
            plt.Axes.Bottom.TickLabelStyle.Rotation = -60;
            plt.Axes.Bottom.TickLabelStyle.Bold = true;
            plt.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;
            plt.Axes.Bottom.TickLabelStyle.ForeColor = Colors.Black;
            plt.Axes.Bottom.MinimumSize = 120;

            // Set Y-axis
            plt.Axes.Left.Label.Text = "Probability per $ (%)";
            plt.Axes.Left.Min = 0;
            plt.Axes.Margins(bottom: 0);

            // Set title
            plt.Title("Price Interval Probability Distribution");

            // now, let's add some additional vertical lines over the chart
            // first line is CURRENT PRICE.
            var currentPriceXMapping = AlignXOnChart(normal_probs.Select((z) => z.Key).ToList(), underlyingPrice);
            var line = plt.Add.VerticalLine(currentPriceXMapping, 2, color: Colors.Red);
            line.LabelText = $"Current: ${underlyingPrice}";
            line.LabelAlignment = Alignment.UpperCenter;
            line.LabelOppositeAxis = true;

            // second line is KDE most likely price.
            var kdeMLPXMapping = AlignXOnChart(normal_probs.Select((z) => z.Key).ToList(), (decimal)kdeMLP);
            line = plt.Add.VerticalLine(kdeMLPXMapping, 2, color: Colors.Green);
            line.LabelText = $"Expected (KDE): ${kdeMLP}";
            line.LabelAlignment = Alignment.UpperCenter;
            line.LabelOppositeAxis = true;

            // additionally, plot KDE distribution
            // first, map X values to the first chart
            var kdeXValues = kde.xValues.Select((x) => AlignXOnChart(normal_probs.Select((z) => z.Key).ToList(), (decimal)x)).ToArray();
            var kdePlot = plt.Add.Scatter(kdeXValues, kde.kdeValues);
            kdePlot.MarkerStyle.IsVisible = false;
            kdePlot.LineStyle.Width = 2;

            // Render and show plot
            plt.SavePng($"interval_probabilities_{deltaOrModel}.png", 1920, 1080);

            return (kdeMLP, kde.xValues, kde.kdeValues);
        }

        public static double AlignXOnChart(List<decimal> values, decimal value)
        {
            var initialX = (double)values.IndexOf(values.First((z) => value <= z)) - 1;

            // adjust X more precisely
            for (int i = 0; i < values.Count - 1; i++)
            {
                var first = values.ElementAt(i);
                var second = values.ElementAt(i + 1);

                if (first <= value && second >= value)
                {
                    // adjust currentPriceXMapping to the range
                    var half = (second - first) / 2;
                    var middle = first + half;

                    initialX += (double)((value - middle) / half / 2);
                }
            }

            return initialX;
        }

        public static async Task<List<BybitOptionTicker>> UpdateOptionsAndTickerAsync(string coin)
        {
            // get all USDC options
            var options = await rest.V5Api.ExchangeData.GetOptionTickersAsync(
                baseAsset: coin
                );

            var optionsList = options.Data.List.Where((z) => !z.Symbol.Contains("USDT")).ToList();

            // sort all options by expiration date, then by strike price, then C/P
            var sorted = optionsList.OrderBy((z) =>
            {
                var t = ParseOption(z.Symbol);
                return t.Item1;
            }).ThenBy((x) =>
            {
                var t = ParseOption(x.Symbol);
                return t.Item2;
            }).ThenBy((c) =>
            {
                var t = ParseOption(c.Symbol);
                return t.Item3;
            }).ToList();

            // get underlying price
            var underlying = await rest.V5Api.ExchangeData.GetSpotTickersAsync(
                symbol: $"{coin}USDT"
                );
            underlyingPrice = underlying.Data.List.First().LastPrice;

            return sorted;
        }

        public static (double[] xValues, double[] kdeValues) KDE((double, double)[] values, int k = 2)
        {
            // Extract prices and cumulative probabilities
            var prices = values.Select(d => d.Item1).ToArray();
            var cumulativeProbs = values.Select(d => d.Item2).ToArray();

            // Compute the probability mass function (PMF) from cumulative probabilities
            var pmf = new double[cumulativeProbs.Length - 1];
            for (int i = 0; i < pmf.Length; i++)
            {
                pmf[i] = Math.Abs(cumulativeProbs[i + 1] - cumulativeProbs[i]);
            }
            var pmfPrices = prices.Take(prices.Length - 1).ToArray();

            // Define KDE evaluation points
            double[] xValues = Enumerable.Range(0, 1000)
                                .Select(i => prices.Min() + i * (prices.Max() - prices.Min()) / 999.0)
                                .ToArray();

            // Compute adaptive bandwidths using k-nearest neighbors
            double[] bandwidths = new double[pmfPrices.Length];
            for (int i = 0; i < pmfPrices.Length; i++)
            {
                var distances = pmfPrices.Select(p => Math.Abs(p - pmfPrices[i])).Where(d => d > 0).OrderBy(d => d).ToArray();
                bandwidths[i] = (distances.Length >= k ? distances[k - 1] : distances.LastOrDefault(5000)) * 0.8d; // Default bandwidth if insufficient neighbors
            }

            // Compute KDE using adaptive bandwidths
            double[] kdeValues = xValues.Select(x =>
            {
                double density = 0.0;
                for (int i = 0; i < pmfPrices.Length; i++)
                {
                    double h = bandwidths[i]; // Adaptive bandwidth for this data point
                    double exponent = -0.5 * Math.Pow((x - pmfPrices[i]) / h, 2);
                    double gaussianKernel = (1.0 / (h * Math.Sqrt(2 * Math.PI))) * Math.Exp(exponent);
                    density += pmf[i] * gaussianKernel;
                }
                return density;
            }).ToArray();

            return (xValues, kdeValues);
        }


        public static double Ptouch(BybitOptionTicker option, decimal s0)
        {
            var t = ParseOption(option.Symbol);
            var r = 0.05;
            var expiryInDays = Math.Round((t.Item1 - DateTime.UtcNow).TotalDays, 1) / 365d;
            var sigma = (double)option.MarkIv;

            double d1 = (Math.Log((double)s0 / (double)t.Item2) + (r + 0.5 * Math.Pow(sigma, 2)) * expiryInDays) / (sigma * Math.Sqrt(expiryInDays));

            double P_touch;
            if (t.Item3 == 'P')
            {
                P_touch = 2 * Normal.CDF(0, 1, -d1);
            }
            else if (t.Item3 == 'C')
            {
                P_touch = 2 * Normal.CDF(0, 1, d1);
            }
            else
            {
                throw new ArgumentException("Option should be either 'C' or 'P'.");
            }

            return Math.Clamp(P_touch, 0, 1);
        }

        public static void SC(ConsoleColor color, string msg)
        {
            Console.ForegroundColor = color;
            Console.Write(msg);
        }
        public static void RC() => Console.ResetColor();

        public static Tuple<DateTime, decimal, char> ParseOption(string option)
        {
            string format = "dMMMyy";

            var splet = option.Split('-');
            string expiry = splet[1];
            string strike = splet[2];
            string cp = splet[3];

            var expiryDate = DateTime.ParseExact(expiry, format, CultureInfo.InvariantCulture);
            var expiryDateUTC = new DateTime(expiryDate.Year, expiryDate.Month, expiryDate.Day, 8, 0, 0);
            var strikePrice = decimal.Parse(strike);

            return new Tuple<DateTime, decimal, char>(expiryDateUTC, strikePrice, cp.First());
        }

        public static string Prompt(string message)
        {
            Console.Write($"{message}: ");
            return Console.ReadLine();
        }
    }
}
