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
        public bool UseTrailing { get; set; } // Kept, but not used for drawing TS line

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
        private double buyTrailing, sellTrailing; // Calculated, but not drawn
        private double buyTrailingTrigger, sellTrailingTrigger; // Field for BE Trigger

        // --- UI Elements ---
        private Button btnBuy;
        private Button btnSell;

        // Add state variables
        private bool isBuyChecked = false;
        private bool isSellChecked = false;     
        
        // ====================================================================
        // LIFECYCLE METHODS
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
            // REMOVED BUY_TR and SELL_TR from the line removal list.
            string[] lines = { "BUY_Entry", "BUY_SL", "BUY_TP", "BUY_BE", "BUY_BE_Trigger",
                               "SELL_Entry", "SELL_SL", "SELL_TP", "SELL_BE", "SELL_BE_Trigger" };
            foreach (var line in lines)
            {
                Chart.RemoveObject(line);
                Chart.RemoveObject(line + "_Label");
            }
            // Ensure the TS line is explicitly removed in case it was drawn previously
            Chart.RemoveObject("BUY_TR");
            Chart.RemoveObject("BUY_TR_Label");
            Chart.RemoveObject("SELL_TR");
            Chart.RemoveObject("SELL_TR_Label");


            if (isBuyChecked)
                DrawPreviewLevels(TradeType.Buy);

            if (isSellChecked)
                DrawPreviewLevels(TradeType.Sell);
        }

        protected override void OnTick()
        {
            if (isBuyChecked || isSellChecked)
            {
                UpdatePreviewLevels();
                UpdatePreviewDisplay();
            }
        }
        
        // ====================================================================
        // SWING PRICE HELPER METHODS
        // ====================================================================
        private double GetRecentLow(int lookbackBars)
        {
            return Bars.LowPrices.Skip(Math.Max(0, Bars.Count - lookbackBars)).Min();
        }

        private double GetRecentHigh(int lookbackBars)
        {
            return Bars.HighPrices.Skip(Math.Max(0, Bars.Count - lookbackBars)).Max();
        }

        // ====================================================================
        // LEVEL CALCULATION LOGIC (Aligned with MoTech.cs)
        // ====================================================================
        private void UpdatePreviewLevels()
        {
            double atrVal = HistoricalAtrValue > 0 ? HistoricalAtrValue : atr?.Result.LastValue ?? Symbol.PipSize * 10;
            double buffer = atrVal * 0.25; 
            double breakEvenBuffer = 2 * Symbol.PipSize; 
            
            // ===================== BUY =====================
            buyEntry = EntryPrice == 0 ? Symbol.Ask : EntryPrice;

            // SL Calculation (tighter of ATR or Swing - buffer)
            double buySlPureAtr = buyEntry - atrVal * SlAtrMultiplier;
            double swingPriceLow = GetRecentLow(SwingLookbackBars); 
            buySL = Math.Min(buySlPureAtr, swingPriceLow - buffer); 
            buySL = Math.Round(buySL / Symbol.TickSize) * Symbol.TickSize; 

            // TP Calculation (wider of ATR or Swing + buffer)
            double swingPriceHigh = GetRecentHigh(SwingLookbackBars); 
            buyTP = Math.Max(buyEntry + atrVal * TpAtrMultiplier, swingPriceHigh + buffer);
            buyTP = Math.Round(buyTP / Symbol.TickSize) * Symbol.TickSize; 

            // Break-even Price (Entry + 2 pips)
            buyBreakEven = buyEntry + breakEvenBuffer; 
            buyBreakEven = Math.Round(buyBreakEven / Symbol.TickSize) * Symbol.TickSize;

            // Trailing Trigger (Profit distance required to move to BE)
            buyTrailingTrigger = buyEntry + (atrVal * BreakEvenAtrMultiplier);
            buyTrailingTrigger = Math.Round(buyTrailingTrigger / Symbol.TickSize) * Symbol.TickSize;
            
            // Trailing Stop Level (High - ATR * Multiplier) - Calculated but NOT DRAWN
            buyTrailing = Bars.HighPrices.LastValue - atrVal * TrailingAtrMultiplier;
            buyTrailing = Math.Round(buyTrailing / Symbol.TickSize) * Symbol.TickSize;


            // ===================== SELL =====================
            sellEntry = EntryPrice == 0 ? Symbol.Bid : EntryPrice;

            // SL Calculation (tighter of ATR or Swing + buffer)
            double sellSlPureAtr = sellEntry + atrVal * SlAtrMultiplier;
            swingPriceHigh = GetRecentHigh(SwingLookbackBars); 
            sellSL = Math.Max(sellSlPureAtr, swingPriceHigh + buffer); 
            sellSL = Math.Round(sellSL / Symbol.TickSize) * Symbol.TickSize; 

            // TP Calculation (wider of ATR or Swing - buffer)
            swingPriceLow = GetRecentLow(SwingLookbackBars); 
            sellTP = Math.Min(sellEntry - atrVal * TpAtrMultiplier, swingPriceLow - buffer);
            sellTP = Math.Round(sellTP / Symbol.TickSize) * Symbol.TickSize; 

            // Break-even Price (Entry - 2 pips)
            sellBreakEven = sellEntry - breakEvenBuffer; 
            sellBreakEven = Math.Round(sellBreakEven / Symbol.TickSize) * Symbol.TickSize;

            // Trailing Trigger (Profit distance required to move to BE)
            sellTrailingTrigger = sellEntry - (atrVal * BreakEvenAtrMultiplier);
            sellTrailingTrigger = Math.Round(sellTrailingTrigger / Symbol.TickSize) * Symbol.TickSize;

            // Trailing Stop Level (Low + ATR * Multiplier) - Calculated but NOT DRAWN
            sellTrailing = Bars.LowPrices.LastValue + atrVal * TrailingAtrMultiplier;
            sellTrailing = Math.Round(sellTrailing / Symbol.TickSize) * Symbol.TickSize;
        }

        // ====================================================================
        // DRAWING LOGIC (Only Entry, SL, TP, BE Price, and BE Trigger)
        // ====================================================================
        private void DrawPreviewLevels(TradeType type)
        {
            if (type == TradeType.Buy)
            {
                // Lines
                Chart.DrawHorizontalLine("BUY_Entry", buyEntry, Color.Blue, 1, LineStyle.Dots);
                Chart.DrawHorizontalLine("BUY_SL", buySL, Color.Red, 1, LineStyle.Lines);
                Chart.DrawHorizontalLine("BUY_TP", buyTP, Color.Green, 1, LineStyle.LinesDots);
                Chart.DrawHorizontalLine("BUY_BE", buyBreakEven, Color.Orange, 1, LineStyle.Lines);
                Chart.DrawHorizontalLine("BUY_BE_Trigger", buyTrailingTrigger, Color.DarkOrange, 1, LineStyle.Dots);

                // Labels
                Chart.DrawText("BUY_Entry_Label", $"Entry: {buyEntry:F5}", Bars.Count - 1, buyEntry, Color.Blue);
                Chart.DrawText("BUY_SL_Label", $"SL: {buySL:F5}", Bars.Count - 1, buySL, Color.Red);
                Chart.DrawText("BUY_TP_Label", $"TP: {buyTP:F5}", Bars.Count - 1, buyTP, Color.Green);
                Chart.DrawText("BUY_BE_Label", $"BE Price: {buyBreakEven:F5}", Bars.Count - 1, buyBreakEven, Color.Orange);
                Chart.DrawText("BUY_BE_Trigger_Label", $"BE Trigger: {buyTrailingTrigger:F5}", Bars.Count - 1, buyTrailingTrigger, Color.DarkOrange);
            }
            else if (type == TradeType.Sell)
            {
                // Lines
                Chart.DrawHorizontalLine("SELL_Entry", sellEntry, Color.Blue, 1, LineStyle.Dots);
                Chart.DrawHorizontalLine("SELL_SL", sellSL, Color.Red, 1, LineStyle.Lines);
                Chart.DrawHorizontalLine("SELL_TP", sellTP, Color.Green, 1, LineStyle.LinesDots);
                Chart.DrawHorizontalLine("SELL_BE", sellBreakEven, Color.Orange, 1, LineStyle.Lines);
                Chart.DrawHorizontalLine("SELL_BE_Trigger", sellTrailingTrigger, Color.DarkOrange, 1, LineStyle.Dots);

                // Labels
                Chart.DrawText("SELL_Entry_Label", $"Entry: {sellEntry:F5}", Bars.Count - 1, sellEntry, Color.Blue);
                Chart.DrawText("SELL_SL_Label", $"SL: {sellSL:F5}", Bars.Count - 1, sellSL, Color.Red);
                Chart.DrawText("SELL_TP_Label", $"TP: {sellTP:F5}", Bars.Count - 1, sellTP, Color.Green);
                Chart.DrawText("SELL_BE_Label", $"BE Price: {sellBreakEven:F5}", Bars.Count - 1, sellBreakEven, Color.Orange);
                Chart.DrawText("SELL_BE_Trigger_Label", $"BE Trigger: {sellTrailingTrigger:F5}", Bars.Count - 1, sellTrailingTrigger, Color.DarkOrange);
            }
        }
    }
}