using System;
using System.Linq; 
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.API.Requests;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MOTechBot : Robot
    {
        // === External Parameters ===
        [Parameter("Risk (%) per trade", DefaultValue = 1.0)]
        public double RiskPercent { get; set; }

        [Parameter("ATR Multiplier (SL)", DefaultValue = 1.5)]
        public double SlAtrMultiplier { get; set; }

        [Parameter("ATR Multiplier (TP1)", DefaultValue = 2.5)]
        public double TpAtrMultiplier { get; set; }

        [Parameter("Pyramiding Distance (pips)", DefaultValue = 120)]
        public double PyramidingDistancePips { get; set; }

        [Parameter("Enable Trailing Stop", DefaultValue = true)]
        public bool UseTrailing { get; set; }

        [Parameter("Trailing ATR Multiplier", DefaultValue = 1.0)]
        public double TrailingAtrMultiplier { get; set; }

        [Parameter("Break-even ATR Multiplier", DefaultValue = 1.0)]
        public double BreakEvenAtrMultiplier { get; set; }

        [Parameter("Partial TP %", DefaultValue = 50)]
        public double PartialTpPercent { get; set; }

        [Parameter("Trailing Step (pips)", DefaultValue = 5)]
        public double TrailingStepPips { get; set; }

        // === Constants ===
        private const string BotLabel = "MOTechBot_";
        private const int AtrPeriod = 14;
        private const int EmaShortPeriod = 21;
        private const int EmaMidPeriod = 50;
        private const int EmaLongPeriod = 200;
        private const int WeeklyEmaPeriod = 50;
        private const int RsiPeriod = 21;
        private const int MacdFast = 12;
        private const int MacdSlow = 26;
        private const int MacdSignal = 9;
        private const int PullbackLookbackBars = 3;
        private const int TicksToWait = 5;

        // === Indicators ===
        private ExponentialMovingAverage emaShort, emaMid, emaLong, weeklyEma;
        private RelativeStrengthIndex rsi;
        private AverageTrueRange atr;
        private MacdCrossOver macd;
        private Bars weeklyBars;

        // === State Variables ===
        private DateTime lastHeartbeat;
        private DateTime lastEntryDate; 
        private int dataSyncTicks = 0;
        private bool isDataSynchronized = false;

        // --- Preview Prices ---
        private double buyEntry, sellEntry;
        private double buySL, buyTP, sellSL, sellTP;
        private double buyBreakEven, sellBreakEven;
        private double buyTrailing, sellTrailing;

        // --- Buttons ---
        private Button btnBuy;
        private Button btnSell;

        // Add state variables
        private bool isBuyChecked = false;
        private bool isSellChecked = false;        

        // === Restored Positions Tracking ===
        private class RestoredPosition
        {
            public Position Position { get; set; }
            public double? StopLoss { get; set; }
            public double? TakeProfit { get; set; }
            public int Step { get; set; } // Pyramiding step (from comment)
        }
        private Dictionary<long, RestoredPosition> activePositions = new Dictionary<long, RestoredPosition>();

        protected override void OnStart()
        {            
            InitializeButtons();
            // --- Restore open positions from broker ---
            RestoreOpenPositions();

            // --- Initialize indicators ---
            emaShort = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaShortPeriod);
            emaMid = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaMidPeriod);
            emaLong = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaLongPeriod);

            rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            macd = Indicators.MacdCrossOver(MacdSlow, MacdFast, MacdSignal);

            weeklyBars = MarketData.GetBars(TimeFrame.Weekly, Symbol.Name);
            weeklyEma = Indicators.ExponentialMovingAverage(weeklyBars.ClosePrices, WeeklyEmaPeriod);

            Positions.Closed += OnPositionClosed;

            lastHeartbeat = Server.Time;
            Print("[START] MOTechBot started on {0}. Waiting {1} ticks to sync position data.", Symbol.Name, TicksToWait);

            EvaluateLastClosedBar(1);
            LogCurrentConditions("startup", 1);
        }
        
        private void InitializeButtons()
        {
            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                BackgroundColor = Color.Gold,
                Opacity = 0.7,
                Margin = 10
            };

            btnBuy = new Button { Text = "BUY", Width = 60, Height = 30, Margin = 5 };
            btnBuy.Click += BtnBuy_Click;
            stackPanel.AddChild(btnBuy);

            btnSell = new Button { Text = "SELL", Width = 60, Height = 30, Margin = 5 };
            btnSell.Click += BtnSell_Click;
            stackPanel.AddChild(btnSell);

            Chart.AddControl(stackPanel);
        }
        
        private void BtnBuy_Click(ButtonClickEventArgs obj)
        {
            isBuyChecked = !isBuyChecked;  // toggle state
            UpdatePreviewDisplay();
        }

        private void BtnSell_Click(ButtonClickEventArgs obj)
        {
            isSellChecked = !isSellChecked;  // toggle state
            UpdatePreviewDisplay();
        }

        // Update preview display based on current toggles
        private void UpdatePreviewDisplay()
        {
            // Remove all old lines and labels first
            string[] lines = { "BUY_Entry", "BUY_SL", "BUY_TP", "BUY_BE", "BUY_TR",
                               "SELL_Entry", "SELL_SL", "SELL_TP", "SELL_BE", "SELL_TR" };
            foreach (var line in lines)
            {
                Chart.RemoveObject(line);
                Chart.RemoveObject(line + "_Label");
            }

            if (isBuyChecked)
                DrawPreviewLevels(TradeType.Buy);

            if (isSellChecked)
                DrawPreviewLevels(TradeType.Sell);
        }        

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
            Print("[STOP] MOTechBot stopped on {0}", Symbol.Name);
        }

        protected override void OnBarClosed()
        {
            EvaluateLastClosedBar(0);

            // Daily heartbeat log
            if ((Server.Time - lastHeartbeat).TotalDays >= 1)
            {
                LogCurrentConditions("heartbeat", 0);
                lastHeartbeat = Server.Time;
            }
        }

        protected override void OnTick()
        {
            if (atr == null)
                atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            // --- Update preview levels if at least one button is toggled ---
            if (isBuyChecked || isSellChecked)
            {
                UpdatePreviewLevels();
                UpdatePreviewDisplay();
            }


            // --- Synchronization Check ---
            if (!isDataSynchronized)
            {
                dataSyncTicks++;
                if (dataSyncTicks >= TicksToWait)
                {
                    isDataSynchronized = true;
                    Print($"[DATA SYNC] Position data synchronized.");
                }
            }

            ApplyPartialTakeProfit();

            if (UseTrailing)
                ApplyTrailingStopsWithBreakEven(atr.Result.LastValue);
        }
        
        // === Restore open positions on bot restart ===
        private void RestoreOpenPositions()
        {
            var positions = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name);

            foreach (var pos in positions)
            {
                int step = 0;
                if (pos.Comment.StartsWith("Pyramid_Step_"))
                    int.TryParse(pos.Comment.Replace("Pyramid_Step_", ""), out step);

                // Compute safe SL/TP using ATR if missing
                double atrVal = atr?.Result.LastValue ?? Symbol.PipSize * 10;
                double safeSL = pos.TradeType == TradeType.Buy
                    ? pos.EntryPrice - atrVal * SlAtrMultiplier
                    : pos.EntryPrice + atrVal * SlAtrMultiplier;
                double safeTP = pos.TradeType == TradeType.Buy
                    ? pos.EntryPrice + atrVal * TpAtrMultiplier
                    : pos.EntryPrice - atrVal * TpAtrMultiplier;

                var restored = new RestoredPosition
                {
                    Position = pos,
                    StopLoss = pos.StopLoss ?? safeSL,
                    TakeProfit = pos.TakeProfit ?? safeTP,
                    Step = step
                };

                activePositions[pos.Id] = restored;

                // Immediately correct broker-side position if SL/TP is null
                if (!pos.StopLoss.HasValue || !pos.TakeProfit.HasValue)
                {
                    //ModifyPosition(pos, restored.StopLoss, restored.TakeProfit, ProtectionType.None);
                    pos.ModifyStopLossPrice(restored.StopLoss);
                    pos.ModifyTakeProfitPrice(restored.TakeProfit);
                }
            }

            Print($"[RESTORE] {activePositions.Count} positions restored.");
        }

        // === Core Strategy Methods ===
        private void EvaluateLastClosedBar(int index)
        {
            int dailyIndex = index;
            int weeklyIndex = 1;

            double close = Bars.ClosePrices.Last(dailyIndex);
            bool bullishTrend = close > emaMid.Result.Last(dailyIndex) && close > emaLong.Result.Last(dailyIndex);
            bool bearishTrend = close < emaMid.Result.Last(dailyIndex) && close < emaLong.Result.Last(dailyIndex);

            if (weeklyBars.Count <= weeklyIndex) return;

            double weeklyEmaVal = weeklyEma.Result.Last(weeklyIndex);
            double weeklyClose = weeklyBars.ClosePrices.Last(weeklyIndex);
            bool weeklyBullish = weeklyClose > weeklyEmaVal;
            bool weeklyBearish = weeklyClose < weeklyEmaVal;

            double rsiVal = rsi.Result.Last(dailyIndex);
            double macdHist = macd.Histogram.Last(dailyIndex);

            if (bullishTrend && weeklyBullish)
                CheckAndEnterTrade(TradeType.Buy, close, GetRecentLow(5), rsiVal, macdHist, dailyIndex);

            if (bearishTrend && weeklyBearish)
                CheckAndEnterTrade(TradeType.Sell, close, GetRecentHigh(5), rsiVal, macdHist, dailyIndex);
        }

        private void CheckAndEnterTrade(TradeType tradeType, double entryPrice, double swingPrice, double rsiVal, double macdHist, int dailyIndex)
        {
            var positions = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name, tradeType);
            int currentStep = positions.Length;

            // Initial trade restriction
            if (currentStep == 0 && Bars.OpenTimes.Last(dailyIndex).Date == lastEntryDate.Date)
            {
                Print("[SKIP] Initial trade already executed today.");
                return;
            }

            // Pyramiding distance check
            if (currentStep > 0)
            {
                double lastEntryPrice = positions.OrderByDescending(p => p.EntryTime).First().EntryPrice;
                if (Math.Abs(entryPrice - lastEntryPrice) < PyramidingDistancePips * Symbol.PipSize)
                {
                    Print("[SKIP] Pyramiding skipped. Price too close to last entry.");
                    return;
                }
            }

            // Pullback / strong trend logic
            double emaSlope = Math.Abs(emaShort.Result.Last(dailyIndex) - emaShort.Result.Last(dailyIndex + 1));
            double atrVal = atr.Result.Last(dailyIndex);
            double dynamicBuffer = Math.Max(emaShort.Result.Last(dailyIndex) * 0.002, atrVal * 0.3) + emaSlope;

            bool touchedEmaShort = false;
            for (int i = 0; i < PullbackLookbackBars; i++)
            {
                if (tradeType == TradeType.Buy && Bars.LowPrices.Last(i) <= emaShort.Result.Last(i) + dynamicBuffer) touchedEmaShort = true;
                if (tradeType == TradeType.Sell && Bars.HighPrices.Last(i) >= emaShort.Result.Last(i) - dynamicBuffer) touchedEmaShort = true;
            }

            bool strongTrendEntry = false;
            bool trendAligned = tradeType == TradeType.Buy
                ? entryPrice > emaMid.Result.Last(dailyIndex) && entryPrice > emaLong.Result.Last(dailyIndex)
                : entryPrice < emaMid.Result.Last(dailyIndex) && entryPrice < emaLong.Result.Last(dailyIndex);

            bool strongMomentumOk = tradeType == TradeType.Buy
                ? rsiVal > 60 && macdHist > 0
                : rsiVal < 40 && macdHist < 0;

            if (trendAligned && strongMomentumOk)
            {
                double minDistance = entryPrice * 0.003; 
                if ((tradeType == TradeType.Buy && entryPrice > emaShort.Result.Last(dailyIndex) + minDistance) ||
                    (tradeType == TradeType.Sell && entryPrice < emaShort.Result.Last(dailyIndex) - minDistance))
                    strongTrendEntry = true;
            }

            if (strongTrendEntry)
            {
                ExecuteTrendMaxTrade(tradeType, entryPrice, swingPrice, currentStep);
                return;
            }

            if (touchedEmaShort)
            {
                bool pullbackMomentumOk = tradeType == TradeType.Buy
                    ? rsiVal > 50 && macdHist > 0
                    : rsiVal < 50 && macdHist < 0;

                if (pullbackMomentumOk)
                    ExecuteTrendMaxTrade(tradeType, entryPrice, swingPrice, currentStep);
            }
        }

        private void ExecuteTrendMaxTrade(TradeType tradeType, double entryPrice, double swingPrice, int stepNumber)
        {
            double atrVal = atr.Result.LastValue;
            double buffer = atrVal * 0.25;

            double slPrice = tradeType == TradeType.Buy
                ? Math.Min(entryPrice - atrVal * SlAtrMultiplier, swingPrice - buffer)
                : Math.Max(entryPrice + atrVal * SlAtrMultiplier, swingPrice + buffer);

            slPrice = Math.Round(slPrice / Symbol.TickSize) * Symbol.TickSize;
            double stopLossPips = Math.Max(Symbol.PipSize * 2, Math.Abs(entryPrice - slPrice)) / Symbol.PipSize; 

            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            double riskCostPerMinLot = stopLossPips * Symbol.PipValue * Symbol.VolumeInUnitsMin;
            if (riskCostPerMinLot <= 0) riskCostPerMinLot = double.MaxValue;

            double numberOfMinLots = Math.Floor(riskAmount / riskCostPerMinLot);
            double finalVolume = Math.Min(numberOfMinLots * Symbol.VolumeInUnitsMin,
                                          Math.Floor(Account.FreeMargin / Symbol.GetEstimatedMargin(tradeType, Symbol.VolumeInUnitsMin)) * Symbol.VolumeInUnitsMin);

            finalVolume = Symbol.NormalizeVolumeInUnits(Math.Max(finalVolume, Symbol.VolumeInUnitsMin), RoundingMode.ToNearest);

            double tp1Price = tradeType == TradeType.Buy
                ? entryPrice + atrVal * TpAtrMultiplier
                : entryPrice - atrVal * TpAtrMultiplier;

            tp1Price = Math.Round(tp1Price / Symbol.TickSize) * Symbol.TickSize;

            string positionComment = stepNumber == 0 ? "Initial_Entry" : $"Pyramid_Step_{stepNumber}";

            if (finalVolume < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] Final volume below minimum after checks.");
                return;
            }

            var result = ExecuteMarketOrder(tradeType, Symbol.Name, finalVolume, $"{BotLabel}{Symbol.Name}",
                                             (int)Math.Round(stopLossPips), tp1Price, positionComment);

            if (result.IsSuccessful)
            {
                lastEntryDate = Bars.OpenTimes.Last(0).Date;
                Print("[ENTRY] {0} @ {1:F5}, SL={2:F5}, TP={3:F5}, Comment={4}, Volume={5:F2}",
                      tradeType, entryPrice, slPrice, tp1Price, positionComment, finalVolume);
            }
            else
            {
                Print("[FAILURE] Market Order FAILED: {0}", result.Error);
            }
        }

        private void ApplyPartialTakeProfit()
        {
            foreach (var pos in Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name))
            {
                if (!pos.TakeProfit.HasValue) 
                    continue;  // Skip if TP is missing

                double currentPrice = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

                bool tpHit = pos.TradeType == TradeType.Buy
                    ? currentPrice >= pos.TakeProfit.Value
                    : currentPrice <= pos.TakeProfit.Value;

                if (tpHit)
                {
                    double volumeToClose = pos.VolumeInUnits * PartialTpPercent / 100.0;
                    volumeToClose = Math.Min(Math.Max(volumeToClose, Symbol.VolumeInUnitsMin), pos.VolumeInUnits);
                    volumeToClose = Symbol.NormalizeVolumeInUnits(volumeToClose, RoundingMode.ToNearest);

                    var closeResult = ClosePositionPartial(pos, volumeToClose);

                    if (closeResult != null && closeResult.IsSuccessful)
                    {
                        double breakEvenBuffer = 2 * Symbol.PipSize;
                        double breakEvenSL = pos.TradeType == TradeType.Buy
                            ? pos.EntryPrice + breakEvenBuffer
                            : pos.EntryPrice - breakEvenBuffer;

                        breakEvenSL = Math.Round(breakEvenSL / Symbol.TickSize) * Symbol.TickSize;

                        var modifyResult = ModifyPosition(pos, breakEvenSL, null, ProtectionType.None); 
                        if (modifyResult.IsSuccessful)
                            Print("[RISK MANAGED] Position {0} moved to Break-Even SL: {1:F5}. TP NULL flagged.", pos.Id, breakEvenSL);
                    }
                }
            }
        }

        private TradeResult ClosePositionPartial(Position position, double volumeToClose)
        {
            if (volumeToClose <= 0) return null;

            var result = ClosePosition(position, volumeToClose);

            if (result.IsSuccessful)
                Print("[PARTIAL PROFIT] Closed {0:F2} units of {1} @ {2:F5}", volumeToClose, position.Id, Symbol.Bid);
            else
                Print("[WARNING] Partial close failed for {0}: {1}", position.Id, result.Error);

            return result;
        }

        private void ApplyTrailingStopsWithBreakEven(double atrVal)
        {
            // === Define necessary variables for checking ===
            double minChangeInPips = 1; 
            
            // Convert the broker's minimum stop level from pips to price units
            double minDistanceInPrice = Symbol.MinStopLossDistance * Symbol.PipSize; 

            // Calculate the required profit (in Pips) before any Break-Even move is allowed.
            // This correctly uses the BreakEvenAtrMultiplier parameter.
            double pipsToTriggerBE = atrVal * BreakEvenAtrMultiplier / Symbol.PipSize;

            foreach (var pos in Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name))
            {
                if (!pos.StopLoss.HasValue)
                    continue;

                // --- Compute total costs in account currency ---
                double totalCommission = pos.Commissions; 
                double totalSwap = pos.Swap; 
                double totalCost = totalCommission + totalSwap;

                // Convert total cost to price adjustment in symbol price
                // pos.VolumeInUnits * Symbol.PipValue is essentially Volume * PointValue
                double priceAdjustment = totalCost / (pos.VolumeInUnits * Symbol.PipValue);

                // Small buffer to prevent early BE hit (2 pips in price units)
                double breakEvenBuffer = 0; //2 * Symbol.PipSize; 

                if (pos.TradeType == TradeType.Buy)
                {
                    // ========================================================================
                    // 1. BREAK-EVEN LOGIC (BUY)
                    // ========================================================================
                    if (pos.Pips >= pipsToTriggerBE) 
                    {
                        // Calculate the TARGET Break-Even SL price (Entry + Costs + Buffer)
                        double targetBreakEvenSL = pos.EntryPrice + breakEvenBuffer + priceAdjustment;

                        // Determine the MAXIMUM valid SL price based on the broker's Stop Level.
                        // SL for BUY is triggered by BID price, so SL must be minDistanceInPrice BELOW current BID.
                        double maxValidSLPrice = Symbol.Bid - minDistanceInPrice; 

                        // The FINAL proposed SL must be the lower of the two values to ensure validity.
                        double finalNewSL = Math.Min(targetBreakEvenSL, maxValidSLPrice);
                        
                        // CHECK 2: Is the proposed SL an improvement over the current SL by at least the minimum change?
                        if (finalNewSL > pos.StopLoss.Value + Symbol.PipSize * minChangeInPips)
                        {
                            Print("MARIUS BREAK EVEN");
                            // Attempt modification with the broker-compliant SL price
                            AttemptModifySL(pos, finalNewSL, pos.TakeProfit, "Break-Even Move");
                        }
                    }
                    
                    // ========================================================================
                    // 2. TRAILING STOP LOGIC (BUY)
                    // ========================================================================
                    if (pos.StopLoss.Value >= pos.EntryPrice)
                    {
                        // Calculate new potential trailing stop price (Current Bid - ATR trailing distance)
                        double targetTrailingSL = Symbol.Bid - atrVal * TrailingAtrMultiplier;

                        // Determine the MAXIMUM valid SL price based on the broker's Stop Level.
                        // SL must be minDistanceInPrice BELOW current BID.
                        double maxValidSLPrice = Symbol.Bid - minDistanceInPrice; 

                        // The FINAL proposed SL must be the lower of the two values to ensure validity.
                        double finalNewSL = Math.Min(targetTrailingSL, maxValidSLPrice);

                        // CHECK: Is the proposed new SL an improvement over the current SL by at least the step size?
                        if (finalNewSL > pos.StopLoss.Value + TrailingStepPips * Symbol.PipSize)
                        {
                            // Attempt modification with the broker-compliant SL price
                            AttemptModifySL(pos, finalNewSL, pos.TakeProfit, "Trailing Stop");
                        }
                    }
                }
                else // TradeType.Sell
                {
                    // ========================================================================
                    // 1. BREAK-EVEN LOGIC (SELL)
                    // ========================================================================
                    if (pos.Pips >= pipsToTriggerBE)
                    {
                        // Calculate the TARGET Break-Even SL price (Entry - Costs - Buffer)
                        double targetBreakEvenSL = pos.EntryPrice - breakEvenBuffer - priceAdjustment;
                        
                        // Determine the MINIMUM valid SL price based on the broker's Stop Level.
                        // SL for SELL is triggered by ASK price, so SL must be minDistanceInPrice ABOVE current ASK.
                        double minValidSLPrice = Symbol.Ask + minDistanceInPrice; 
                        
                        // The FINAL proposed SL must be the higher of the two values to ensure validity.
                        // We want the lowest possible price that is still valid.
                        double finalNewSL = Math.Max(targetBreakEvenSL, minValidSLPrice);
                        
                        // CHECK 2: Is the proposed SL an improvement over the current SL by at least the minimum change?
                        // For SELL, a lower price is an improvement.
                        if (finalNewSL < pos.StopLoss.Value - Symbol.PipSize * minChangeInPips)
                        {
                            // Attempt modification with the broker-compliant SL price
                            AttemptModifySL(pos, finalNewSL, pos.TakeProfit, "Break-Even Move");
                        }
                    }
                    
                    // ========================================================================
                    // 2. TRAILING STOP LOGIC (SELL)
                    // ========================================================================
                    if (pos.StopLoss.Value <= pos.EntryPrice)
                    {
                        // Calculate new potential trailing stop price (Current Ask + ATR trailing distance)
                        double targetTrailingSL = Symbol.Ask + atrVal * TrailingAtrMultiplier;

                        // Determine the MINIMUM valid SL price based on the broker's Stop Level.
                        // SL must be minDistanceInPrice ABOVE current ASK.
                        double minValidSLPrice = Symbol.Ask + minDistanceInPrice; 
                        
                        // The FINAL proposed SL must be the higher of the two values to ensure validity.
                        double finalNewSL = Math.Max(targetTrailingSL, minValidSLPrice);

                        // CHECK: Is the proposed new SL an improvement over the current SL by at least the step size?
                        // For SELL, a lower price is an improvement.
                        if (finalNewSL < pos.StopLoss.Value - TrailingStepPips * Symbol.PipSize)
                        {
                            // Attempt modification with the broker-compliant SL price
                            AttemptModifySL(pos, finalNewSL, pos.TakeProfit, "Trailing Stop");
                        }
                    }
                }
            }
        }

        private void AttemptModifySL(Position pos, double newSL, double? currentTP, string reason)
        {
            // Ensure SL is rounded and logical
            newSL = Math.Round(newSL / Symbol.TickSize) * Symbol.TickSize;

            if (pos.TradeType == TradeType.Buy && newSL < pos.EntryPrice)
                newSL = pos.EntryPrice;
            if (pos.TradeType == TradeType.Sell && newSL > pos.EntryPrice)
                newSL = pos.EntryPrice;

            // Use safe TP if null
            double safeTP;
            if (!currentTP.HasValue)
            {
                double atrVal = atr?.Result.LastValue ?? Symbol.PipSize * 10;
                safeTP = pos.TradeType == TradeType.Buy
                    ? pos.EntryPrice + atrVal * TpAtrMultiplier
                    : pos.EntryPrice - atrVal * TpAtrMultiplier;
            }
            else
                safeTP = currentTP.Value;

            var result = pos.ModifyStopLossPrice(newSL);
            
            if (result.IsSuccessful)
                Print("[MODIFY SL] Position {0} SL changed from {1:F5} to {2:F5} due to {3}", 
                      pos.Id, pos.StopLoss, newSL, reason);
            else
                Print("[MODIFY SL FAILED] Position {0} attempted SL change to {1:F5}: {2}", 
                      pos.Id, newSL, result.Error);
        }

        private double GetRecentLow(int lookbackBars)
        {
            return Bars.LowPrices.Skip(Math.Max(0, Bars.Count - lookbackBars)).Min();
        }

        private double GetRecentHigh(int lookbackBars)
        {
            return Bars.HighPrices.Skip(Math.Max(0, Bars.Count - lookbackBars)).Max();
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;

            // Determine final exit price based on trade type
            double exitPrice = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

            // NetProfit is already calculated by cTrader, GrossProfit includes commission and swaps
            Print("[EXIT] {0} {1} | Entry={2:F5} | Exit={3:F5} | Volume={4:F2} | Net P/L={5:F2} | Gross P/L={6:F2} | Reason={7} | Comment={8}",
                pos.TradeType,
                pos.SymbolName,
                pos.EntryPrice,
                exitPrice,
                pos.VolumeInUnits,
                pos.NetProfit,
                pos.GrossProfit,
                args.Reason,
                pos.Comment
            );
        }

        private void LogCurrentConditions(string source, int index)
        {
            int dailyIndex = index;
            int weeklyIndex = 1;

            double close = Bars.ClosePrices.Last(dailyIndex);

            // Safety check for weekly data
            if (weeklyBars.Count <= weeklyIndex)
            {
                Print($"[{source.ToUpper()}] Conditions log skipped: Not enough weekly bar data.");
                return;
            }

            double weeklyClose = weeklyBars.ClosePrices.Last(weeklyIndex);
            double emaShortVal = emaShort.Result.Last(dailyIndex);
            double emaMidVal = emaMid.Result.Last(dailyIndex);
            double emaLongVal = emaLong.Result.Last(dailyIndex);
            double weeklyEmaVal = weeklyEma.Result.Last(weeklyIndex);
            double rsiVal = rsi.Result.Last(dailyIndex);
            double macdHist = macd.Histogram.Last(dailyIndex);

            bool weeklyBullish = weeklyClose > weeklyEmaVal;
            bool weeklyBearish = weeklyClose < weeklyEmaVal;
            bool dailyBullish = close > emaMidVal && close > emaLongVal;
            bool dailyBearish = close < emaMidVal && close < emaLongVal;

            // Simplified "touch" check
            bool touchedEmaShort = false;
            for (int i = 0; i < PullbackLookbackBars; i++)
            {
                if (Bars.LowPrices.Last(i) <= emaShort.Result.Last(i) || Bars.HighPrices.Last(i) >= emaShort.Result.Last(i))
                {
                    touchedEmaShort = true;
                    break;
                }
            }

            bool hasBuyToday = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name, TradeType.Buy)
                                         .Any(p => p.EntryTime.Date == Server.Time.Date);
            bool hasSellToday = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name, TradeType.Sell)
                                          .Any(p => p.EntryTime.Date == Server.Time.Date);

            string entry;
            if (hasBuyToday) entry = "✅ BUY (Existing Position)";
            else if (hasSellToday) entry = "✅ SELL (Existing Position)";
            else entry = dailyBullish && weeklyBullish && touchedEmaShort && rsiVal > 50 && macdHist > 0 ? "✅ BUY" :
                         dailyBearish && weeklyBearish && touchedEmaShort && rsiVal < 50 && macdHist < 0 ? "✅ SELL" :
                         "⛔ NO ENTRY";

            string Check(bool cond) => cond ? "☑️" : "⬜";

            string buyConditions = $"{Check(dailyBullish)} DailyTrend=BULLISH, {Check(weeklyBullish)} WeeklyTrend=BULLISH, {Check(touchedEmaShort)} EMA Short Touch, {Check(rsiVal > 50)} RSI=({rsiVal:F1} > 50), {Check(macdHist > 0)} MACD=({macdHist:F4} > 0)";
            string sellConditions = $"{Check(dailyBearish)} DailyTrend=BEARISH, {Check(weeklyBearish)} WeeklyTrend=BEARISH, {Check(touchedEmaShort)} EMA Short Touch, {Check(rsiVal < 50)} RSI=({rsiVal:F1} < 50), {Check(macdHist < 0)} MACD=({macdHist:F4} < 0)";

            Print($"{Symbol.Name} {Server.Time:dd-MM-yyyy} [{entry}] [BUY: {buyConditions}] [SELL: {sellConditions}] [Current Close: {close:F5}]");
        }

        // Estimate the commission per trade unit (volumeInUnits) using Symbol and Account settings
        private double EstimateCommission(TradeType tradeType, double volumeInUnits)
        {
            double comm = 0;

            switch (Symbol.CommissionType)
            {
                case SymbolCommissionType.PercentageOfTradingVolume:
                    comm = Symbol.Commission / 100.0 * volumeInUnits * Symbol.Bid;
                    break;

                case SymbolCommissionType.QuoteCurrencyPerOneLot:
                    comm = Symbol.Commission * (volumeInUnits / Symbol.VolumeInUnitsMin);
                    comm = Math.Max(comm, Symbol.MinCommission);
                    break;

                case SymbolCommissionType.UsdPerMillionUsdVolume:
                    comm = Symbol.Commission * (volumeInUnits * Symbol.Bid / 1_000_000);
                    comm = Math.Max(comm, Symbol.MinCommission);
                    break;

                case SymbolCommissionType.UsdPerOneLot:
                    comm = Symbol.Commission * (volumeInUnits / Symbol.VolumeInUnitsMin);
                    comm = Math.Max(comm, Symbol.MinCommission);
                    break;
            }

            return comm;
        }

        private void UpdatePreviewLevels()
        {
            // --- Ensure ATR is ready ---
            double atrVal = atr?.Result.LastValue ?? Symbol.PipSize * 10;

            // ===================== BUY =====================
            buyEntry = Symbol.Ask;

            // SL (ensure below entry)
            buySL = buyEntry - atrVal * SlAtrMultiplier;
            buySL = Math.Min(buySL, buyEntry - Symbol.PipSize); // must be below entry
            buySL = Math.Round(buySL / Symbol.TickSize) * Symbol.TickSize;

            // TP (ensure above entry)
            buyTP = buyEntry + atrVal * TpAtrMultiplier;
            buyTP = Math.Round(buyTP / Symbol.TickSize) * Symbol.TickSize;

            // Break-even (ensure above entry)
            buyBreakEven = buyEntry + atrVal * BreakEvenAtrMultiplier;
            buyBreakEven = Math.Max(buyBreakEven, buyEntry + Symbol.PipSize); // must be above entry
            buyBreakEven = Math.Round(buyBreakEven / Symbol.TickSize) * Symbol.TickSize;

            // Trailing (dynamic preview)
            buyTrailing = buyEntry + atrVal * TrailingAtrMultiplier;
            buyTrailing = Math.Round(buyTrailing / Symbol.TickSize) * Symbol.TickSize;

            // ===================== SELL =====================
            sellEntry = Symbol.Bid;

            // SL (ensure above entry)
            sellSL = sellEntry + atrVal * SlAtrMultiplier;
            sellSL = Math.Max(sellSL, sellEntry + Symbol.PipSize); // must be above entry
            sellSL = Math.Round(sellSL / Symbol.TickSize) * Symbol.TickSize;

            // TP (ensure below entry)
            sellTP = sellEntry - atrVal * TpAtrMultiplier;
            sellTP = Math.Round(sellTP / Symbol.TickSize) * Symbol.TickSize;

            // Break-even (ensure below entry)
            sellBreakEven = sellEntry - atrVal * BreakEvenAtrMultiplier;
            sellBreakEven = Math.Min(sellBreakEven, sellEntry - Symbol.PipSize); // must be below entry
            sellBreakEven = Math.Round(sellBreakEven / Symbol.TickSize) * Symbol.TickSize;

            // Trailing (dynamic preview)
            sellTrailing = sellEntry - atrVal * TrailingAtrMultiplier;
            sellTrailing = Math.Round(sellTrailing / Symbol.TickSize) * Symbol.TickSize;
        }

        private void DrawPreviewLevels(TradeType type)
        {
            if (type == TradeType.Buy)
            {
                if (buyEntry > 0) Chart.DrawHorizontalLine("BUY_Entry", buyEntry, Color.Blue, 1, LineStyle.Dots);
                if (buySL > 0) Chart.DrawHorizontalLine("BUY_SL", buySL, Color.Red, 1, LineStyle.Lines);
                if (buyTP > 0) Chart.DrawHorizontalLine("BUY_TP", buyTP, Color.Green, 1, LineStyle.LinesDots);
                if (buyBreakEven > 0) Chart.DrawHorizontalLine("BUY_BE", buyBreakEven, Color.Orange, 1, LineStyle.Lines);
                if (buyTrailing > 0) Chart.DrawHorizontalLine("BUY_TR", buyTrailing, Color.Purple, 1, LineStyle.LinesDots);

                if (buySL > 0) Chart.DrawText("BUY_SL_Label", $"SL: {buySL:F5}", Bars.Count - 1, buySL, Color.Red);
                if (buyTP > 0) Chart.DrawText("BUY_TP_Label", $"TP: {buyTP:F5}", Bars.Count - 1, buyTP, Color.Green);
                if (buyBreakEven > 0) Chart.DrawText("BUY_BE_Label", $"BE: {buyBreakEven:F5}", Bars.Count - 1, buyBreakEven, Color.Orange);
                if (buyTrailing > 0) Chart.DrawText("BUY_TR_Label", $"TR: {buyTrailing:F5}", Bars.Count - 1, buyTrailing, Color.Purple);
                Chart.DrawText("BUY_Entry_Label", $"Price: {buyEntry:F5}", Bars.Count - 1, buyEntry, Color.Blue);
            }
            else if (type == TradeType.Sell)
            {
                if (sellEntry > 0) Chart.DrawHorizontalLine("SELL_Entry", sellEntry, Color.Blue, 1, LineStyle.Dots);
                if (sellSL > 0) Chart.DrawHorizontalLine("SELL_SL", sellSL, Color.Red, 1, LineStyle.Lines);
                if (sellTP > 0) Chart.DrawHorizontalLine("SELL_TP", sellTP, Color.Green, 1, LineStyle.LinesDots);
                if (sellBreakEven > 0) Chart.DrawHorizontalLine("SELL_BE", sellBreakEven, Color.Orange, 1, LineStyle.Lines);
                if (sellTrailing > 0) Chart.DrawHorizontalLine("SELL_TR", sellTrailing, Color.Purple, 1, LineStyle.LinesDots);

                if (sellSL > 0) Chart.DrawText("SELL_SL_Label", $"SL: {sellSL:F5}", Bars.Count - 1, sellSL, Color.Red);
                if (sellTP > 0) Chart.DrawText("SELL_TP_Label", $"TP: {sellTP:F5}", Bars.Count - 1, sellTP, Color.Green);
                if (sellBreakEven > 0) Chart.DrawText("SELL_BE_Label", $"BE: {sellBreakEven:F5}", Bars.Count - 1, sellBreakEven, Color.Orange);
                if (sellTrailing > 0) Chart.DrawText("SELL_TR_Label", $"TR: {sellTrailing:F5}", Bars.Count - 1, sellTrailing, Color.Purple);
                Chart.DrawText("SELL_Entry_Label", $"Price: {sellEntry:F5}", Bars.Count - 1, sellEntry, Color.Blue);
            }
        }
    }
}
