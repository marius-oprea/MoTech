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

        [Parameter("Max Pyramid Steps", DefaultValue = 3)]
        public int MaxPyramidSteps { get; set; }

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

        [Parameter("Remove TP After Pyramid", DefaultValue = true)]
        public bool RemoveTpAfterPyramid { get; set; }

        [Parameter("Trail TP on Bar Close", DefaultValue = false)]
        public bool TrailTpOnBarClose { get; set; }

        [Parameter("TP Trail ATR Multiplier", DefaultValue = 2.0)]
        public double TpTrailAtrMultiplier { get; set; }

        // === Constants ===
        private const string BotLabel = "MOTechBot_";
        private const int HigherTfEmaPeriod = 50;
        private const int PullbackLookbackBars = 3;
        private const int TicksToWait = 5;

        // === Indicator Periods (will be set dynamically) ===
        private int AtrPeriod;
        private int EmaShortPeriod;
        private int EmaMidPeriod;
        private int EmaLongPeriod;
        private int RsiPeriod;
        private int MacdFast;
        private int MacdSlow;
        private int MacdSignal;

        // === Indicator Profile ===
        private class IndicatorProfile
        {
            public int EmaShort { get; set; }
            public int EmaMid { get; set; }
            public int EmaLong { get; set; }
            public int Rsi { get; set; }
            public int Atr { get; set; }
            public int MacdFast { get; set; }
            public int MacdSlow { get; set; }
            public int MacdSignal { get; set; }
        }

        // === Indicators ===
        private ExponentialMovingAverage emaShort, emaMid, emaLong, higherTfEma;
        private RelativeStrengthIndex rsi;
        private AverageTrueRange atr;
        private MacdCrossOver macd;
        private Bars higherTfBars;
        private TimeFrame higherTimeframe;

        // === State Variables ===
        private int dataSyncTicks = 0;
        private bool isDataSynchronized = false;

        // === Restored Positions Tracking ===
        private class RestoredPosition
        {
            public Position Position { get; set; }
            public double? StopLoss { get; set; }
            public double? TakeProfit { get; set; }
            public int Step { get; set; }
            public bool BreakEvenApplied { get; set; } = false;
            public double LastTrailingSL { get; set; } = 0;
            public bool IsLocked { get; set; } = false;  // NEW: Prevents trailing when next step opens
            public double LockedAtPrice { get; set; } = 0;  // NEW: Price where SL was locked
        }
        private Dictionary<long, RestoredPosition> activePositions = new Dictionary<long, RestoredPosition>();

        // ========================================================================
        // LIFECYCLE METHODS
        // ========================================================================

        protected override void OnStart()
        {
            // Load indicator profile for current timeframe
            var profile = GetIndicatorProfile();
            AtrPeriod = profile.Atr;
            EmaShortPeriod = profile.EmaShort;
            EmaMidPeriod = profile.EmaMid;
            EmaLongPeriod = profile.EmaLong;
            RsiPeriod = profile.Rsi;
            MacdFast = profile.MacdFast;
            MacdSlow = profile.MacdSlow;
            MacdSignal = profile.MacdSignal;
            
            Print("[PROFILE] TF:{0} | EMA:{1}/{2}/{3} | RSI:{4} | ATR:{5} | MACD:{6}/{7}/{8}",
                  TimeFrame, EmaShortPeriod, EmaMidPeriod, EmaLongPeriod, 
                  RsiPeriod, AtrPeriod, MacdFast, MacdSlow, MacdSignal);
            
            // Determine higher timeframe
            higherTimeframe = GetHigherTimeframe();
            Print("[INIT] Current TF: {0}, Higher TF: {1}", TimeFrame, higherTimeframe);
            
            // Initialize indicators with adaptive periods
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            emaShort = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaShortPeriod);
            emaMid = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaMidPeriod);
            emaLong = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaLongPeriod);
            rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            macd = Indicators.MacdCrossOver(MacdSlow, MacdFast, MacdSignal);

            higherTfBars = MarketData.GetBars(higherTimeframe, Symbol.Name);
            higherTfEma = Indicators.ExponentialMovingAverage(higherTfBars.ClosePrices, HigherTfEmaPeriod);

            // Subscribe to events
            Positions.Closed += OnPositionClosed;

            // Restore existing positions
            RestoreOpenPositions();

            Print("[START] MOTechBot started on {0}. Waiting {1} ticks to sync.", Symbol.Name, TicksToWait);
            
            // Initial evaluation
            LogCurrentConditions("startup", 1);
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
            Print("[STOP] MOTechBot stopped on {0}", Symbol.Name);
        }

        protected override void OnBarClosed()
        {
            int index = 0; // Last closed bar
            
            // 1. Check for trend reversals and close positions if needed
            CheckAndCloseTrendReversals(index);
            
            // 2. Evaluate new entry opportunities
            EvaluateEntries(index);
            
            // 3. Log current market conditions
            LogCurrentConditions("bar_closed", index);
        }

        protected override void OnTick()
        {
            // Wait for data synchronization
            if (!isDataSynchronized)
            {
                dataSyncTicks++;
                if (dataSyncTicks >= TicksToWait)
                {
                    isDataSynchronized = true;
                    Print("[DATA SYNC] Position data synchronized.");
                }
                return;
            }

            // Manage open positions (partial TP, trailing stops)
            double atrVal = atr.Result.LastValue;
            ManageOpenPositions(atrVal);
        }

        // ========================================================================
        // ENTRY LOGIC
        // ========================================================================

        private IndicatorProfile GetIndicatorProfile()
        {
            // === 1-MINUTE CHART ===
            if (TimeFrame == TimeFrame.Minute)
                return new IndicatorProfile
                {
                    EmaShort = 200,   // ~3+ hours of data
                    EmaMid = 500,     // ~8+ hours
                    EmaLong = 1200,   // ~20 hours
                    Rsi = 14,         // Standard fast RSI
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === 5-MINUTE CHART ===
            if (TimeFrame == TimeFrame.Minute5)
                return new IndicatorProfile
                {
                    EmaShort = 100,   // ~8 hours
                    EmaMid = 200,     // ~16 hours
                    EmaLong = 500,    // ~40+ hours
                    Rsi = 14,
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === 15-MINUTE CHART ===
            if (TimeFrame == TimeFrame.Minute15)
                return new IndicatorProfile
                {
                    EmaShort = 50,    // ~12 hours
                    EmaMid = 100,     // ~24 hours
                    EmaLong = 200,    // ~50 hours
                    Rsi = 14,
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === 30-MINUTE CHART ===
            if (TimeFrame == TimeFrame.Minute30)
                return new IndicatorProfile
                {
                    EmaShort = 40,    // ~20 hours
                    EmaMid = 80,      // ~40 hours
                    EmaLong = 200,    // ~100 hours
                    Rsi = 14,
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === 1-HOUR CHART ===
            if (TimeFrame == TimeFrame.Hour)
                return new IndicatorProfile
                {
                    EmaShort = 21,    // ~21 hours (~1 day)
                    EmaMid = 50,      // ~2 days
                    EmaLong = 200,    // ~8 days
                    Rsi = 14,
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === 4-HOUR CHART ===
            if (TimeFrame == TimeFrame.Hour4)
                return new IndicatorProfile
                {
                    EmaShort = 21,    // ~3.5 days
                    EmaMid = 50,      // ~8 days
                    EmaLong = 200,    // ~33 days
                    Rsi = 21,         // Longer for stability
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === DAILY CHART (BASELINE) ===
            if (TimeFrame == TimeFrame.Daily)
                return new IndicatorProfile
                {
                    EmaShort = 21,    // ~1 month
                    EmaMid = 50,      // ~2.5 months
                    EmaLong = 200,    // ~10 months
                    Rsi = 21,
                    Atr = 14,
                    MacdFast = 12,
                    MacdSlow = 26,
                    MacdSignal = 9
                };

            // === WEEKLY CHART ===
            if (TimeFrame == TimeFrame.Weekly)
                return new IndicatorProfile
                {
                    EmaShort = 10,    // ~10 weeks (~2.5 months)
                    EmaMid = 20,      // ~20 weeks (~5 months)
                    EmaLong = 50,     // ~50 weeks (~1 year)
                    Rsi = 14,
                    Atr = 10,
                    MacdFast = 8,     // Faster MACD for weekly
                    MacdSlow = 17,
                    MacdSignal = 9
                };

            // === MONTHLY CHART ===
            if (TimeFrame == TimeFrame.Monthly)
                return new IndicatorProfile
                {
                    EmaShort = 6,     // ~6 months
                    EmaMid = 12,      // ~1 year
                    EmaLong = 24,     // ~2 years
                    Rsi = 14,
                    Atr = 6,
                    MacdFast = 6,
                    MacdSlow = 12,
                    MacdSignal = 6
                };

            // === DEFAULT FALLBACK (Daily settings) ===
            Print($"[WARNING] Unknown timeframe {TimeFrame}. Using Daily profile.");
            return new IndicatorProfile
            {
                EmaShort = 21,
                EmaMid = 50,
                EmaLong = 200,
                Rsi = 21,
                Atr = 14,
                MacdFast = 12,
                MacdSlow = 26,
                MacdSignal = 9
            };
        }

        private TimeFrame GetHigherTimeframe()
        {
            // Auto-detect next higher timeframe based on current timeframe
            return GetNextTimeFrame(TimeFrame);
        }

        private TimeFrame GetNextTimeFrame(TimeFrame currentTimeFrame)
        {
            if (currentTimeFrame == TimeFrame.Minute) return TimeFrame.Minute5;
            if (currentTimeFrame == TimeFrame.Minute5) return TimeFrame.Minute15;
            if (currentTimeFrame == TimeFrame.Minute15) return TimeFrame.Minute30;
            if (currentTimeFrame == TimeFrame.Minute30) return TimeFrame.Hour;
            if (currentTimeFrame == TimeFrame.Hour) return TimeFrame.Hour4;
            if (currentTimeFrame == TimeFrame.Hour4) return TimeFrame.Daily;
            if (currentTimeFrame == TimeFrame.Daily) return TimeFrame.Weekly;
            if (currentTimeFrame == TimeFrame.Weekly) return TimeFrame.Monthly;
            
            Print($"[WARNING] Unsupported timeframe {currentTimeFrame}. Defaulting HTF to Weekly.");
            return TimeFrame.Weekly; // Default for unsupported timeframes
        }

        private void EvaluateEntries(int index)
        {
            if (Bars.Count <= index + 1 || higherTfBars.Count <= 1) return;

            double close = Bars.ClosePrices.Last(index);
            double higherTfClose = higherTfBars.ClosePrices.Last(1);
            double higherTfEmaVal = higherTfEma.Result.Last(1);
            
            bool currentTfBullish = close > emaMid.Result.Last(index) && close > emaLong.Result.Last(index);
            bool currentTfBearish = close < emaMid.Result.Last(index) && close < emaLong.Result.Last(index);
            bool higherTfBullish = higherTfClose > higherTfEmaVal;
            bool higherTfBearish = higherTfClose < higherTfEmaVal;
            
            double rsiVal = rsi.Result.Last(index);
            double macdHist = macd.Histogram.Last(index);
            
            // Check for buy opportunity
            if (currentTfBullish && higherTfBullish)
                TryEnterTrade(TradeType.Buy, close, rsiVal, macdHist, index);
            
            // Check for sell opportunity
            if (currentTfBearish && higherTfBearish)
                TryEnterTrade(TradeType.Sell, close, rsiVal, macdHist, index);
        }

        private void TryEnterTrade(TradeType tradeType, double entryPrice, double rsiVal, double macdHist, int index)
        {
            // Get existing pyramid positions for this trade type
            var pyramidPositions = activePositions.Values
                .Where(p => p.Position.TradeType == tradeType)
                .OrderBy(p => p.Step)
                .ToList();
            
            int stepNumber = pyramidPositions.Count;
            
            // Check if we've reached maximum pyramid steps
            if (stepNumber >= MaxPyramidSteps)
            {
                Print("[SKIP] Max pyramid steps ({0}) reached for {1}.", MaxPyramidSteps, tradeType);
                return;
            }
            
            // If pyramiding, check conditions
            if (stepNumber > 0)
            {
                var lastStep = pyramidPositions.Last();
                
                // CRITICAL: Only allow new pyramid if last step is at break-even
                if (!lastStep.BreakEvenApplied)
                {
                    Print("[SKIP] Last pyramid step not at break-even yet.");
                    return;
                }
                
                // Check minimum distance from last entry
                double lastEntryPrice = lastStep.Position.EntryPrice;
                if (Math.Abs(entryPrice - lastEntryPrice) < PyramidingDistancePips * Symbol.PipSize)
                {
                    Print("[SKIP] Too close to last pyramid entry ({0} pips required).", PyramidingDistancePips);
                    return;
                }
            }
            
            // Check if entry conditions are met
            bool hasValidSetup = CheckEntryConditions(tradeType, entryPrice, rsiVal, macdHist, index);
            
            if (hasValidSetup)
            {
                double swingPrice = tradeType == TradeType.Buy ? GetRecentLow(5) : GetRecentHigh(5);
                ExecuteTrade(tradeType, entryPrice, swingPrice, stepNumber);
            }
        }

        private bool CheckEntryConditions(TradeType tradeType, double entryPrice, double rsiVal, double macdHist, int index)
        {
            double atrVal = atr.Result.Last(index);
            double emaSlope = Math.Abs(emaShort.Result.Last(index) - emaShort.Result.Last(index + 1));
            double dynamicBuffer = Math.Max(emaShort.Result.Last(index) * 0.002, atrVal * 0.3) + emaSlope;
            
            // Check for pullback to EMA21
            bool touchedEmaShort = false;
            for (int i = 0; i < PullbackLookbackBars && i < Bars.Count; i++)
            {
                if (tradeType == TradeType.Buy && Bars.LowPrices.Last(i) <= emaShort.Result.Last(i) + dynamicBuffer)
                    touchedEmaShort = true;
                if (tradeType == TradeType.Sell && Bars.HighPrices.Last(i) >= emaShort.Result.Last(i) - dynamicBuffer)
                    touchedEmaShort = true;
            }
            
            bool trendAligned = tradeType == TradeType.Buy
                ? entryPrice > emaMid.Result.Last(index) && entryPrice > emaLong.Result.Last(index)
                : entryPrice < emaMid.Result.Last(index) && entryPrice < emaLong.Result.Last(index);
            
            bool momentumOk = tradeType == TradeType.Buy
                ? rsiVal > 50 && macdHist > 0
                : rsiVal < 50 && macdHist < 0;
            
            // Strong trend entry (RSI > 60 or < 40, no pullback needed)
            bool strongMomentum = tradeType == TradeType.Buy 
                ? rsiVal > 60 && macdHist > 0 
                : rsiVal < 40 && macdHist < 0;
                
            if (trendAligned && strongMomentum)
            {
                double minDistance = entryPrice * 0.003; // 0.3% away from EMA21
                if ((tradeType == TradeType.Buy && entryPrice > emaShort.Result.Last(index) + minDistance) ||
                    (tradeType == TradeType.Sell && entryPrice < emaShort.Result.Last(index) - minDistance))
                {
                    Print("[SETUP] Strong trend continuation detected.");
                    return true;
                }
            }
            
            // Pullback entry (standard setup)
            if (touchedEmaShort && momentumOk)
            {
                Print("[SETUP] Valid pullback entry detected.");
                return true;
            }
            
            return false;
        }

        // ========================================================================
        // TRADE EXECUTION
        // ========================================================================

        private void ExecuteTrade(TradeType tradeType, double entryPrice, double swingPrice, int stepNumber)
        {
            double atrVal = atr.Result.LastValue;
            double buffer = atrVal * 0.25;
            
            // Calculate Stop Loss
            double slPrice = tradeType == TradeType.Buy
                ? Math.Min(entryPrice - atrVal * SlAtrMultiplier, swingPrice - buffer)
                : Math.Max(entryPrice + atrVal * SlAtrMultiplier, swingPrice + buffer);
            slPrice = Math.Round(slPrice / Symbol.TickSize) * Symbol.TickSize;
            
            // Calculate Take Profit
            double tpPrice = tradeType == TradeType.Buy
                ? Math.Max(entryPrice + atrVal * TpAtrMultiplier, swingPrice + buffer)
                : Math.Min(entryPrice - atrVal * TpAtrMultiplier, swingPrice - buffer);
            tpPrice = Math.Round(tpPrice / Symbol.TickSize) * Symbol.TickSize;
            
            // Calculate position size based on risk
            double stopLossPips = Math.Abs(entryPrice - slPrice) / Symbol.PipSize;
            double volumeInUnits = CalculateVolume(stopLossPips, tradeType);
            
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] Calculated volume ({0:F2}) below minimum ({1:F2}).", 
                      volumeInUnits, Symbol.VolumeInUnitsMin);
                return;
            }
            
            // Execute the trade
            string comment = stepNumber == 0 ? "Initial_Entry" : $"Pyramid_Step_{stepNumber}";
            
            var result = ExecuteMarketOrder(
                tradeType, 
                Symbol.Name, 
                volumeInUnits, 
                $"{BotLabel}{Symbol.Name}", 
                slPrice, 
                tpPrice, 
                comment
            );
            
            if (result.IsSuccessful)
            {
                // Track the position
                activePositions[result.Position.Id] = new RestoredPosition
                {
                    Position = result.Position,
                    StopLoss = slPrice,
                    TakeProfit = tpPrice,
                    Step = stepNumber,
                    BreakEvenApplied = false,
                    LastTrailingSL = slPrice,
                    IsLocked = false,
                    LockedAtPrice = 0
                };
                
                // If this is a pyramid step (not initial), lock previous step and remove TPs
                if (stepNumber > 0)
                {
                    LockPreviousStepAndRemoveTPs(tradeType, entryPrice, stepNumber);
                }
                
                Print("[ENTRY] {0} Step={1} @ {2:F5}, SL={3:F5}, TP={4:F5}, Vol={5:F2}, Risk={6:F2}%",
                      tradeType, stepNumber, entryPrice, slPrice, tpPrice, volumeInUnits, RiskPercent);
            }
            else
            {
                Print("[ERROR] Trade execution failed: {0}", result.Error);
            }
        }

        private double CalculateVolume(double stopLossPips, TradeType tradeType)
        {
            // Calculate risk amount
            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            
            // Calculate risk per minimum lot
            double riskPerMinLot = stopLossPips * Symbol.PipValue * Symbol.VolumeInUnitsMin;
            if (riskPerMinLot <= 0) return Symbol.VolumeInUnitsMin;
            
            // Calculate number of minimum lots based on risk
            double numberOfMinLots = Math.Floor(riskAmount / riskPerMinLot);
            
            // Check margin constraints
            double estimatedMargin = Symbol.GetEstimatedMargin(tradeType, Symbol.VolumeInUnitsMin);
            double maxAffordableVolume = estimatedMargin > 0 
                ? Math.Floor(Account.FreeMargin / estimatedMargin) * Symbol.VolumeInUnitsMin 
                : double.MaxValue;
            
            // Take the minimum of risk-based and margin-based volume
            double finalVolume = Math.Min(numberOfMinLots * Symbol.VolumeInUnitsMin, maxAffordableVolume);
            finalVolume = Math.Max(Symbol.VolumeInUnitsMin, finalVolume);
            
            return Symbol.NormalizeVolumeInUnits(finalVolume, RoundingMode.ToNearest);
        }

        // ========================================================================
        // POSITION MANAGEMENT
        // ========================================================================

        private void LockPreviousStepAndRemoveTPs(TradeType tradeType, double newEntryPrice, int currentStep)
        {
            var previousSteps = activePositions.Values
                .Where(p => p.Position.TradeType == tradeType && p.Step < currentStep)
                .OrderBy(p => p.Step)
                .ToList();
            
            foreach (var prevStep in previousSteps)
            {
                var pos = prevStep.Position;
                
                // Lock the immediately previous step at new entry price
                if (prevStep.Step == currentStep - 1)
                {
                    double lockPrice = newEntryPrice;
                    lockPrice = Math.Round(lockPrice / Symbol.TickSize) * Symbol.TickSize;
                    
                    // Move SL to new entry price (lock it)
                    var result = pos.ModifyStopLossPrice(lockPrice);
                    if (result.IsSuccessful)
                    {
                        prevStep.IsLocked = true;
                        prevStep.LockedAtPrice = lockPrice;
                        prevStep.LastTrailingSL = lockPrice;
                        Print("[LOCK] Step {0} SL locked at Step {1} entry: {2:F5}", 
                              prevStep.Step, currentStep, lockPrice);
                    }
                }
                
                // Remove TP from all previous steps if enabled
                if (RemoveTpAfterPyramid && pos.TakeProfit.HasValue)
                {
                    pos.ModifyTakeProfitPrice(null);
                    Print("[TP REMOVED] Step {0} TP removed after pyramid", prevStep.Step);
                }
            }
        }

        private void ManageOpenPositions(double atrVal)
        {
            double minDistance = Symbol.MinStopLossDistance * Symbol.PipSize;
            double breakEvenBuffer = 2 * Symbol.PipSize;
            
            // Group positions by trade type for coordinated trailing
            var buyPositions = activePositions.Values
                .Where(p => p.Position.TradeType == TradeType.Buy)
                .OrderBy(p => p.Step)
                .ToList();
            
            var sellPositions = activePositions.Values
                .Where(p => p.Position.TradeType == TradeType.Sell)
                .OrderBy(p => p.Step)
                .ToList();
            
            // Manage buy positions
            if (buyPositions.Any())
                ManagePositionGroup(buyPositions, TradeType.Buy, atrVal, minDistance, breakEvenBuffer);
            
            // Manage sell positions
            if (sellPositions.Any())
                ManagePositionGroup(sellPositions, TradeType.Sell, atrVal, minDistance, breakEvenBuffer);
        }

        private void ManagePositionGroup(List<RestoredPosition> positions, TradeType tradeType, 
                                          double atrVal, double minDistance, double breakEvenBuffer)
        {
            double currentPrice = tradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            
            // Step 1: Apply break-even to positions that haven't reached it yet
            foreach (var restored in positions)
            {
                var pos = restored.Position;
                
                if (!restored.BreakEvenApplied && pos.StopLoss.HasValue)
                {
                    double pipsInProfit = tradeType == TradeType.Buy
                        ? (currentPrice - pos.EntryPrice) / Symbol.PipSize
                        : (pos.EntryPrice - currentPrice) / Symbol.PipSize;
                    
                    double beTriggerPips = (atrVal / Symbol.PipSize) * BreakEvenAtrMultiplier;
                    
                    if (pipsInProfit >= beTriggerPips)
                    {
                        double bePrice = tradeType == TradeType.Buy
                            ? pos.EntryPrice + breakEvenBuffer
                            : pos.EntryPrice - breakEvenBuffer;
                        
                        bePrice = Math.Round(bePrice / Symbol.TickSize) * Symbol.TickSize;
                        
                        // Only move if improving current SL
                        bool shouldMove = tradeType == TradeType.Buy
                            ? bePrice > pos.StopLoss.Value
                            : bePrice < pos.StopLoss.Value;
                        
                        if (shouldMove)
                        {
                            var result = pos.ModifyStopLossPrice(bePrice);
                            if (result.IsSuccessful)
                            {
                                restored.BreakEvenApplied = true;
                                restored.LastTrailingSL = bePrice;
                                Print("[BREAK-EVEN] Step {0} moved to BE @ {1:F5} (Profit: {2:F1} pips)", 
                                      restored.Step, bePrice, pipsInProfit);
                            }
                        }
                    }
                }
            }
            
            // Step 2: Trail unlocked positions (only those not locked by a higher pyramid step)
            if (!UseTrailing) return;
            
            // Calculate trailing price for the group
            double trailPrice = tradeType == TradeType.Buy
                ? Bars.HighPrices.LastValue - atrVal * TrailingAtrMultiplier
                : Bars.LowPrices.LastValue + atrVal * TrailingAtrMultiplier;
            
            trailPrice = Math.Round(trailPrice / Symbol.TickSize) * Symbol.TickSize;
            
            // Trail all unlocked positions together
            foreach (var restored in positions)
            {
                var pos = restored.Position;
                
                // Skip if locked or BE not applied or no SL
                if (restored.IsLocked || !restored.BreakEvenApplied || !pos.StopLoss.HasValue)
                    continue;
                
                // Check if we can trail
                bool canTrail = tradeType == TradeType.Buy
                    ? trailPrice > restored.LastTrailingSL + minDistance
                    : trailPrice < restored.LastTrailingSL - minDistance;
                
                // Ensure we don't trail below entry of higher steps
                if (canTrail)
                {
                    var higherSteps = positions.Where(p => p.Step > restored.Step).ToList();
                    if (higherSteps.Any())
                    {
                        double highestEntry = tradeType == TradeType.Buy
                            ? higherSteps.Max(p => p.Position.EntryPrice)
                            : higherSteps.Min(p => p.Position.EntryPrice);
                        
                        // Don't trail past higher step entries
                        if (tradeType == TradeType.Buy && trailPrice > highestEntry)
                            trailPrice = highestEntry;
                        else if (tradeType == TradeType.Sell && trailPrice < highestEntry)
                            trailPrice = highestEntry;
                    }
                }
                
                if (canTrail)
                {
                    var result = pos.ModifyStopLossPrice(trailPrice);
                    if (result.IsSuccessful)
                    {
                        restored.LastTrailingSL = trailPrice;
                        Print("[TRAILING] Step {0} SL moved to {1:F5}", restored.Step, trailPrice);
                    }
                }
            }
        }

        private void TrailTakeProfits()
        {
            double atrVal = atr.Result.LastValue;
            
            foreach (var restored in activePositions.Values)
            {
                var pos = restored.Position;
                
                // Only trail TP if position has one and is in profit
                if (!pos.TakeProfit.HasValue) continue;
                
                double currentPrice = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                
                // Calculate new TP above current price
                double newTp = pos.TradeType == TradeType.Buy
                    ? currentPrice + atrVal * TpTrailAtrMultiplier
                    : currentPrice - atrVal * TpTrailAtrMultiplier;
                
                newTp = Math.Round(newTp / Symbol.TickSize) * Symbol.TickSize;
                
                // Only move TP if it's closer to price (trailing it down/up)
                bool shouldMove = pos.TradeType == TradeType.Buy
                    ? newTp < pos.TakeProfit.Value && newTp > currentPrice
                    : newTp > pos.TakeProfit.Value && newTp < currentPrice;
                
                if (shouldMove)
                {
                    var result = pos.ModifyTakeProfitPrice(newTp);
                    if (result.IsSuccessful)
                    {
                        Print("[TP TRAIL] Step {0} TP moved to {1:F5}", restored.Step, newTp);
                    }
                }
            }
        }

        private void CheckAndCloseTrendReversals(int index)
        {
            double emaMidVal = emaMid.Result.Last(index);
            double emaLongVal = emaLong.Result.Last(index);
            double close = Bars.ClosePrices.Last(index);
            double rsiVal = rsi.Result.Last(index);
            double macdHist = macd.Histogram.Last(index);
            
            bool currentTfBullish = close > emaMidVal && close > emaLongVal;
            bool currentTfBearish = close < emaMidVal && close < emaLongVal;
            
            foreach (var restored in activePositions.Values.ToList())
            {
                var pos = restored.Position;
                
                // Detect trend reversal with momentum confirmation
                bool reversalDetected = 
                    (pos.TradeType == TradeType.Buy && currentTfBearish && rsiVal < 50 && macdHist < 0) ||
                    (pos.TradeType == TradeType.Sell && currentTfBullish && rsiVal > 50 && macdHist > 0);
                
                if (reversalDetected)
                {
                    var result = ClosePosition(pos);
                    if (result.IsSuccessful)
                    {
                        Print("[REVERSAL] Position {0} closed - Trend reversed from {1}", pos.Id, pos.TradeType);
                        activePositions.Remove(pos.Id);
                    }
                }
            }
        }

        // ========================================================================
        // POSITION RESTORATION
        // ========================================================================

        private void RestoreOpenPositions()
        {
            var positions = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name);

            foreach (var pos in positions)
            {
                // Extract pyramid step from comment
                int step = 0;
                if (pos.Comment.StartsWith("Pyramid_Step_"))
                    int.TryParse(pos.Comment.Replace("Pyramid_Step_", ""), out step);

                double atrVal = atr?.Result.LastValue ?? Symbol.PipSize * 10;

                // Calculate safe default SL/TP if missing
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
                    Step = step,
                    BreakEvenApplied = false,
                    LastTrailingSL = pos.StopLoss ?? safeSL,
                    IsLocked = false,
                    LockedAtPrice = 0
                };

                // Detect if break-even already applied
                if (pos.StopLoss.HasValue)
                {
                    if ((pos.TradeType == TradeType.Buy && pos.StopLoss.Value > pos.EntryPrice) ||
                        (pos.TradeType == TradeType.Sell && pos.StopLoss.Value < pos.EntryPrice))
                    {
                        restored.BreakEvenApplied = true;
                        restored.LastTrailingSL = pos.StopLoss.Value;
                    }
                }

                // Check if position is against current trend (reversal happened while bot was off)
                double currentTfClose = Bars.ClosePrices.LastValue;
                double emaMidVal = emaMid.Result.LastValue;
                double emaLongVal = emaLong.Result.LastValue;
                double rsiVal = rsi.Result.LastValue;
                double macdHist = macd.Histogram.LastValue;

                bool currentTfBullish = currentTfClose > emaMidVal && currentTfClose > emaLongVal && rsiVal > 50 && macdHist > 0;
                bool currentTfBearish = currentTfClose < emaMidVal && currentTfClose < emaLongVal && rsiVal < 50 && macdHist < 0;

                bool oppositeTrend = (pos.TradeType == TradeType.Buy && currentTfBearish) ||
                                     (pos.TradeType == TradeType.Sell && currentTfBullish);

                if (oppositeTrend)
                {
                    var result = ClosePosition(pos);
                    if (result.IsSuccessful)
                    {
                        Print("[RESTORE] Position {0} closed immediately - Opposite trend detected", pos.Id);
                        continue; // Don't add to activePositions
                    }
                }

                // Ensure SL/TP are set
                if (!pos.StopLoss.HasValue || !pos.TakeProfit.HasValue)
                {
                    pos.ModifyStopLossPrice(restored.StopLoss);
                    pos.ModifyTakeProfitPrice(restored.TakeProfit);
                }

                activePositions[pos.Id] = restored;
            }

            Print("[RESTORE] {0} positions restored and validated.", activePositions.Count);
        }

        // ========================================================================
        // EVENT HANDLERS
        // ========================================================================

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (activePositions.ContainsKey(args.Position.Id))
            {
                activePositions.Remove(args.Position.Id);
                Print("[CLOSED] Position {0} removed from tracking.", args.Position.Id);
            }
        }

        // ========================================================================
        // UTILITY METHODS
        // ========================================================================

        private double GetRecentLow(int barsBack)
        {
            return Bars.LowPrices.TakeLast(Math.Min(barsBack, Bars.Count)).Min();
        }

        private double GetRecentHigh(int barsBack)
        {
            return Bars.HighPrices.TakeLast(Math.Min(barsBack, Bars.Count)).Max();
        }

        private void LogCurrentConditions(string source, int index)
        {
            if (higherTfBars.Count <= 1) return;

            double close = Bars.ClosePrices.Last(index);
            double higherTfClose = higherTfBars.ClosePrices.Last(1);
            double emaShortVal = emaShort.Result.Last(index);
            double emaMidVal = emaMid.Result.Last(index);
            double emaLongVal = emaLong.Result.Last(index);
            double higherTfEmaVal = higherTfEma.Result.Last(1);
            double rsiVal = rsi.Result.Last(index);
            double macdHist = macd.Histogram.Last(index);

            bool higherTfBullish = higherTfClose > higherTfEmaVal;
            bool higherTfBearish = higherTfClose < higherTfEmaVal;
            bool currentTfBullish = close > emaMidVal && close > emaLongVal;
            bool currentTfBearish = close < emaMidVal && close < emaLongVal;

            // Check for EMA21 touch
            bool touchedEmaShort = false;
            for (int i = 0; i < PullbackLookbackBars && i < Bars.Count; i++)
            {
                if (Bars.LowPrices.Last(i) <= emaShort.Result.Last(i) || 
                    Bars.HighPrices.Last(i) >= emaShort.Result.Last(i))
                {
                    touchedEmaShort = true;
                    break;
                }
            }

            // Check if we have positions today
            bool hasBuyToday = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name, TradeType.Buy)
                .Any(p => p.EntryTime.Date == Server.Time.Date);
            bool hasSellToday = Positions.FindAll($"{BotLabel}{Symbol.Name}", Symbol.Name, TradeType.Sell)
                .Any(p => p.EntryTime.Date == Server.Time.Date);

            string entry;
            if (hasBuyToday) 
                entry = "✅ BUY (Active)";
            else if (hasSellToday) 
                entry = "✅ SELL (Active)";
            else if (currentTfBullish && higherTfBullish && touchedEmaShort && rsiVal > 50 && macdHist > 0) 
                entry = "✅ BUY Signal";
            else if (currentTfBearish && higherTfBearish && touchedEmaShort && rsiVal < 50 && macdHist < 0) 
                entry = "✅ SELL Signal";
            else 
                entry = "⛔ NO ENTRY";

            string Check(bool cond) => cond ? "☑️" : "⬜";

            string buyConditions = $"{Check(currentTfBullish)} CTF↑, {Check(higherTfBullish)} HTF↑, " +
                                   $"{Check(touchedEmaShort)} EMA21, {Check(rsiVal > 50)} RSI({rsiVal:F1}), " +
                                   $"{Check(macdHist > 0)} MACD({macdHist:F4})";
            
            string sellConditions = $"{Check(currentTfBearish)} CTF↓, {Check(higherTfBearish)} HTF↓, " +
                                    $"{Check(touchedEmaShort)} EMA21, {Check(rsiVal < 50)} RSI({rsiVal:F1}), " +
                                    $"{Check(macdHist < 0)} MACD({macdHist:F4})";

            Print($"[{source.ToUpper()}] {Symbol.Name} {Server.Time:dd-MMM-yyyy} | CTF:{TimeFrame} HTF:{higherTimeframe} | {entry} | " +
                  $"BUY:[{buyConditions}] | SELL:[{sellConditions}] | Close:{close:F5}");
        }
    }
}