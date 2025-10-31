using System;
using System.Linq; 
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.API.Requests;


namespace cAlgo.Robots
{
    // AccessRights.None is crucial, as this bot should not interfere with the live Cloud Bot.
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)] 
    public class MOTechLocalPreview : Robot
    {
        // === External Parameters ===
        [Parameter("Entry Price", DefaultValue = 0.0)]
        public double EntryPrice { get; set; }

        // NEW PARAMETER to fix ATR drift issue
        [Parameter("Historical ATR Value", DefaultValue = 0.0)]
        public double HistoricalAtrValue { get; set; }

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

        // --- Indicator Dependencies ---
        private AverageTrueRange atr;
        private const int AtrPeriod = 14;
        private const int SwingLookbackBars = 5; 

        // --- Preview Prices (Internal Storage) ---
        private double buyEntry, sellEntry;
        private double buySL, buyTP, sellSL, sellTP;
        private double buyBreakEven, sellBreakEven;
        private double buyTrailing, sellTrailing;

        // --- UI Elements ---
        private Button btnBuy;
        private Button btnSell;

        // Add state variables
        private bool isBuyChecked = false;
        private bool isSellChecked = false;     
        
        // ====================================================================
        // INITIALIZATION & BUTTONS
        // ====================================================================
        protected override void OnStart()
        {
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            InitializeButtons();
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
            isBuyChecked = !isBuyChecked;
            UpdatePreviewDisplay();
        }

        private void BtnSell_Click(ButtonClickEventArgs obj)
        {
            isSellChecked = !isSellChecked;
            UpdatePreviewDisplay();
        }

        private void UpdatePreviewDisplay()
        {
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

        // ====================================================================
        // ON TICK - MAIN DRAWING LOOP
        // ====================================================================
        protected override void OnTick()
        {
            // Only update calculations if a preview button is toggled
            if (isBuyChecked || isSellChecked)
            {
                UpdatePreviewLevels();
                UpdatePreviewDisplay();
            }
        }
        
        // ====================================================================
        // SWING PRICE HELPER METHODS (Corrected)
        // ====================================================================
        private double GetRecentLow(int lookbackBars)
        {
            // Finds the lowest low in the lookback period
            return Bars.LowPrices.Skip(Math.Max(0, Bars.Count - lookbackBars)).Min();
        }

        private double GetRecentHigh(int lookbackBars)
        {
            // Finds the highest high in the lookback period
            return Bars.HighPrices.Skip(Math.Max(0, Bars.Count - lookbackBars)).Max();
        }

        // ====================================================================
        // LEVEL CALCULATION LOGIC (Final Alignment with MoTech.cs)
        // ====================================================================
        private void UpdatePreviewLevels()
        {
            // --- Determine ATR Value (Prioritizes Historical Value) ---
            double atrVal = HistoricalAtrValue > 0 ? HistoricalAtrValue : atr?.Result.LastValue ?? Symbol.PipSize * 10;
            double atrBuffer = atrVal * 0.25; 
            double minimumPips = Symbol.PipSize; 

            // ===================== BUY =====================
            buyEntry = EntryPrice == 0 ? Symbol.Ask : EntryPrice;

            // --- SL Calculation with Swing Override (Identical to MoTech.cs) ---
            double buySlPureAtr = buyEntry - atrVal * SlAtrMultiplier;
            double swingPriceLow = GetRecentLow(SwingLookbackBars); 
            
            // 1. Swing Override: Final SL is the tighter of the two distances
            buySL = Math.Min(buySlPureAtr, swingPriceLow - atrBuffer); 
            
            // 2. Minimum Distance Check (Used in MoTech.cs to define stopLossPips)
            buySL = Math.Min(buySL, buyEntry - minimumPips); 
            
            // 3. Final Broker Rounding
            buySL = Math.Round(buySL / Symbol.TickSize) * Symbol.TickSize; 

            // --- TP Calculation (NOW EXACTLY IDENTICAL TO MoTech.cs) ---
            buyTP = buyEntry + atrVal * TpAtrMultiplier;
            // The minimum distance check is removed here to align with live bot
            buyTP = Math.Round(buyTP / Symbol.TickSize) * Symbol.TickSize; 

            // --- Break-even Calculation (Keeping logic from MoTech.cs) ---
            buyBreakEven = buyEntry + atrVal * BreakEvenAtrMultiplier;
            buyBreakEven = Math.Max(buyBreakEven, buyEntry + Symbol.PipSize);
            buyBreakEven = Math.Round(buyBreakEven / Symbol.TickSize) * Symbol.TickSize;

            // --- Trailing Calculation ---
            buyTrailing = buyEntry + atrVal * TrailingAtrMultiplier;
            buyTrailing = Math.Round(buyTrailing / Symbol.TickSize) * Symbol.TickSize;

            // ===================== SELL =====================
            sellEntry = EntryPrice == 0 ? Symbol.Bid : EntryPrice;

            // --- SL Calculation with Swing Override (Identical to MoTech.cs) ---
            double sellSlPureAtr = sellEntry + atrVal * SlAtrMultiplier;
            double swingPriceHigh = GetRecentHigh(SwingLookbackBars); 
            
            // 1. Swing Override: Final SL is the tighter of the two distances
            sellSL = Math.Max(sellSlPureAtr, swingPriceHigh + atrBuffer); 
            
            // 2. Minimum Distance Check (Used in MoTech.cs to define stopLossPips)
            sellSL = Math.Max(sellSL, sellEntry + minimumPips); 
            
            // 3. Final Broker Rounding
            sellSL = Math.Round(sellSL / Symbol.TickSize) * Symbol.TickSize; 

            // --- TP Calculation (NOW EXACTLY IDENTICAL TO MoTech.cs) ---
            sellTP = sellEntry - atrVal * TpAtrMultiplier;
            // The minimum distance check is removed here to align with live bot
            sellTP = Math.Round(sellTP / Symbol.TickSize) * Symbol.TickSize; 

            // --- Break-even Calculation (Keeping logic from MoTech.cs) ---
            sellBreakEven = sellEntry - atrVal * BreakEvenAtrMultiplier;
            sellBreakEven = Math.Min(sellBreakEven, sellEntry - Symbol.PipSize); 
            sellBreakEven = Math.Round(sellBreakEven / Symbol.TickSize) * Symbol.TickSize;

            // --- Trailing Calculation ---
            sellTrailing = sellEntry - atrVal * TrailingAtrMultiplier;
            sellTrailing = Math.Round(sellTrailing / Symbol.TickSize) * Symbol.TickSize;
        }

        // ====================================================================
        // DRAWING LOGIC 
        // ====================================================================
        private void DrawPreviewLevels(TradeType type)
        {
            if (type == TradeType.Buy)
            {
                if (buyEntry > 0) Chart.DrawHorizontalLine("BUY_Entry", buyEntry, Color.Blue, 1, LineStyle.Dots);
                if (buySL > 0) Chart.DrawHorizontalLine("BUY_SL", buySL, Color.Red, 1, LineStyle.Lines);
                if (buyTP > 0) Chart.DrawHorizontalLine("BUY_TP", buyTP, Color.Green, 1, LineStyle.LinesDots);
                if (buyBreakEven > 0) Chart.DrawHorizontalLine("BUY_BE", buyBreakEven, Color.Orange, 1, LineStyle.Lines);
                if (buyTrailing > 0) Chart.DrawHorizontalLine("BUY_TR", buyTrailing, Color.Magenta, 1, LineStyle.LinesDots);

                if (buySL > 0) Chart.DrawText("BUY_SL_Label", $"SL: {buySL:F5}", Bars.Count - 1, buySL, Color.Red);
                if (buyTP > 0) Chart.DrawText("BUY_TP_Label", $"TP: {buyTP:F5}", Bars.Count - 1, buyTP, Color.Green);
                if (buyBreakEven > 0) Chart.DrawText("BUY_BE_Label", $"BE Target: {buyBreakEven:F5}", Bars.Count - 1, buyBreakEven, Color.Orange);
                if (buyTrailing > 0) Chart.DrawText("BUY_TR_Label", $"TR Distance: {buyTrailing:F5}", Bars.Count - 1, buyTrailing, Color.Magenta);
                Chart.DrawText("BUY_Entry_Label", $"Price: {buyEntry:F5}", Bars.Count - 1, buyEntry, Color.Blue);
            }
            else if (type == TradeType.Sell)
            {
                if (sellEntry > 0) Chart.DrawHorizontalLine("SELL_Entry", sellEntry, Color.Blue, 1, LineStyle.Dots);
                if (sellSL > 0) Chart.DrawHorizontalLine("SELL_SL", sellSL, Color.Red, 1, LineStyle.Lines);
                if (sellTP > 0) Chart.DrawHorizontalLine("SELL_TP", sellTP, Color.Green, 1, LineStyle.LinesDots);
                if (sellBreakEven > 0) Chart.DrawHorizontalLine("SELL_BE", sellBreakEven, Color.Orange, 1, LineStyle.Lines);
                if (sellTrailing > 0) Chart.DrawHorizontalLine("SELL_TR", sellTrailing, Color.Magenta, 1, LineStyle.LinesDots);

                if (sellSL > 0) Chart.DrawText("SELL_SL_Label", $"SL: {sellSL:F5}", Bars.Count - 1, sellSL, Color.Red);
                if (sellTP > 0) Chart.DrawText("SELL_TP_Label", $"TP: {sellTP:F5}", Bars.Count - 1, sellTP, Color.Green);
                if (sellBreakEven > 0) Chart.DrawText("SELL_BE_Label", $"BE Target: {sellBreakEven:F5}", Bars.Count - 1, sellBreakEven, Color.Orange);
                if (sellTrailing > 0) Chart.DrawText("SELL_TR_Label", $"TR Distance: {sellTrailing:F5}", Bars.Count - 1, sellTrailing, Color.Magenta);
                Chart.DrawText("SELL_Entry_Label", $"Price: {sellEntry:F5}", Bars.Count - 1, sellEntry, Color.Blue);
            }
        }
    }
}