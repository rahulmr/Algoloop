/*
 * Copyright 2019 Capnode AB
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using System.Diagnostics;
using System.Text;

namespace Algoloop.Algorithm.CSharp.Model
{
    // Portfolio construction scaffolding class; basic method args.
    public class SlotPortfolio : PortfolioConstructionModel
    {
        private readonly bool _logTargets = false;

        private readonly int _slots;
        private readonly bool _reinvest;
        private readonly decimal _rebalance;

        private decimal _initialCapital = 0;
        private decimal _sizingFactor = 1;
        private readonly InsightCollection _insights = new InsightCollection();
        private readonly SimpleMovingAverage _trackerSma;
        private readonly SimpleMovingAverage _benchmarkSma;
        private readonly TrackerPortfolio _trackerPortfolio;

        public SlotPortfolio(
            int slots,
            bool reinvest,
            float rebalance,
            int trackerPeriod = 0,
            int benchmarkPeriod = 0)
        {
            _slots = slots;
            _reinvest = reinvest;
            _rebalance = (decimal)rebalance;

            _trackerPortfolio = new TrackerPortfolio(_slots, _rebalance);
            if (trackerPeriod > 0)
            {
                _trackerSma = new SimpleMovingAverage($"Tracker SMA({trackerPeriod})", trackerPeriod);
            }
            if (benchmarkPeriod > 0)
            {
                _benchmarkSma = new SimpleMovingAverage($"Benchmark SMA({benchmarkPeriod})", benchmarkPeriod);
            }
        }

        // Create list of PortfolioTarget objects from Insights
        public override IEnumerable<IPortfolioTarget> CreateTargets(QCAlgorithm algorithm, Insight[] insights)
        {
            // Save initial capital for future use
            if (_initialCapital == 0)
            {
                _initialCapital = algorithm.Portfolio.Cash;
            }

            // Add to tracker portfolio
            _trackerPortfolio.CreateTargets(algorithm, insights);
            decimal tracker = _trackerPortfolio.GetEquity(algorithm);
            algorithm.Plot("Tracker", tracker);

            // Determine position size dependent on tracking curve
            decimal trackerSizingFactor = 1;
            if (_trackerSma != null)
            {
                // decimal benchmark = algorithm.Benchmark.Evaluate(algorithm.Time);
                _trackerSma.Update(algorithm.Time, tracker);
                if (_trackerSma.IsReady)
                {
                    algorithm.Plot($"TrackerSMA", _trackerSma);
                    trackerSizingFactor = tracker >= _trackerSma ? 1 : 0;
                }
            }

            // Determine position size dependent on benchmark curve
            decimal benchmarkSizingFactor = 1;
            if (_benchmarkSma != null)
            {
                decimal benchmark = algorithm.Benchmark.Evaluate(algorithm.Time);
                _benchmarkSma.Update(algorithm.Time, benchmark);
                if (_benchmarkSma.IsReady)
                {
                    algorithm.Plot($"BenchmarkSMA", _benchmarkSma);
                    benchmarkSizingFactor = benchmark >= _benchmarkSma ? 1 : 0;
                }
            }

            decimal sizingFactor = trackerSizingFactor * benchmarkSizingFactor;
            if (sizingFactor != _sizingFactor)
            {
                algorithm.Log($"Switching to sizing factor {sizingFactor}");
            }
            _sizingFactor = sizingFactor;

            // Calculate number of occupied positions
            int taken = algorithm.Securities.Values
                .Where(m => m.HasData && m.Holdings.Quantity != 0)
                .Count();
            int freeSlots = _slots - taken;
            decimal cash = algorithm.Portfolio.Cash;

            // Create target for new insights
            var targets = new List<IPortfolioTarget>();
            IEnumerable<Insight> toplist = insights.Take(_slots);
            foreach (Insight insight in toplist)
            {
                // Find current holdings and actual target
                Security security = algorithm.Securities[insight.Symbol];
                decimal holdings = security.Holdings.Quantity;
                IPortfolioTarget target;
                if (_reinvest)
                {
                    target = PortfolioTarget.Percent(algorithm, insight.Symbol, (int)insight.Direction * _sizingFactor / _slots);
                    if (holdings == 0 && freeSlots > 0)
                    {
                        decimal amount = Math.Max(_sizingFactor * cash, 0) / freeSlots;
                        decimal size = (int)insight.Direction * decimal.Floor(amount / security.Price);
                        var order = new MarketOrder(insight.Symbol, size, DateTime.UtcNow);
                        OrderFee orderFee = security.FeeModel.GetOrderFee(new OrderFeeParameters(security, order));
                        amount -= decimal.Ceiling(orderFee);
                        amount = Math.Max(amount, 0);
                        decimal quantity = (int)insight.Direction * decimal.Floor(amount / security.Price);
                        if (quantity < target.Quantity)
                        {
                            target = new PortfolioTarget(insight.Symbol, quantity);
                        }
                    }
                }
                else
                {
                    Debug.Assert(security.Price != 0);
                    decimal quantity = (int)insight.Direction
                        * decimal.Floor(_sizingFactor * _initialCapital / _slots / security.Price);
                    target = new PortfolioTarget(insight.Symbol, quantity);
                }

                if (holdings == 0 || target.Quantity == 0)
                {
                    // Create new target
                    targets.Add(target);
                }
                else if (_rebalance > 0 && holdings <=  (1 - _rebalance) * target.Quantity)
                {
                    // Holdings too small, rebalance up
                    algorithm.Log($"Rebalance up {target.Symbol} to quantity={target.Quantity}");
                    targets.Add(target);
                }
                else if (_rebalance > 0 && holdings >= (1 + _rebalance) * target.Quantity)
                {
                    // Holdings too large, rebalance down
                    algorithm.Log($"Rebalance down {target.Symbol} to quantity={target.Quantity}");
                    targets.Add(target);
                }
                else
                {
                    // Remove current insight, add again later
                    _insights.Clear(new[] { insight.Symbol });
                }
            }

            // Close expired insights
            foreach (Insight expired in _insights.RemoveExpiredInsights(algorithm.UtcTime))
            {
                // Add close target if not in target list
                if (!targets.Any(m => m.Symbol.Equals(expired.Symbol)))
                {
                    var target = new PortfolioTarget(expired.Symbol, 0);
                    targets.Add(target);
                }
            }

            // Add toplist in random order
            _insights.AddRange(toplist);

            if (_logTargets)
            {
                LogTargets(algorithm, targets);
            }

            return targets;
        }

        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
        }

        public override string ToString()
        {
            return LogInsights(_insights.OrderByDescending(m => m.CloseTimeUtc).ThenByDescending(m => m.Magnitude));
        }

        private static string LogInsights(IEnumerable<Insight> insights)
        {
            int i = 0;
            var sb = new StringBuilder();
            sb.AppendLine("Insight toplist:");
            foreach (Insight insight in insights)
            {
                string text = $"Insight {++i} {insight.Symbol} {insight.Magnitude:0.########} {insight.Direction}".ToStringInvariant();
                sb.AppendLine(text);
            }

            return sb.ToString();
        }

        private static void LogTargets(QCAlgorithm algorithm, List<IPortfolioTarget> targets)
        {
            int i = 0;
            foreach (IPortfolioTarget target in targets)
            {
                algorithm.Log($"Target {++i} {target.Symbol} quantity={target.Quantity:0.##}".ToStringInvariant());
            }
        }
    }

}
