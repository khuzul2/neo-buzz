// Fork (docs/specs/browser.md, BR1): every result row of the browser can be dragged. The drag carries one BrowserDragData
// (ReBuzz.Agent.Browser), in one application data format, which every drop target reads with <see cref="Read"/>: the Machine View's
// boxes and canvas (BR1), the Arrangement and the Session (DD1).

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ReBuzz.Agent.Browser;

namespace ReBuzz.Studio.Views.Browser
{
    internal static class BrowserDragSource
    {
        public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat(BrowserDragData.FormatName);

        /// <summary>The drag's data as the platform carries it.</summary>
        public static DataTransfer Package(BrowserDragData data)
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(Format, data.Format()));
            return transfer;
        }

        /// <summary>What a drag carries, or null when it is not a browser item (a file from the desktop, text).</summary>
        public static BrowserDragData Read(IDataTransfer transfer) =>
            transfer == null ? null : BrowserDragData.Parse(transfer.TryGetValue(Format));

        /// <summary>The distance the pointer moves with the button down before a row starts a drag.</summary>
        public const double Threshold = 6;

        /// <summary>Makes a row draggable: the left button down and moved <see cref="Threshold"/> starts the drag with what <paramref name="data"/> gives.</summary>
        public static void Attach(Avalonia.Controls.Control row, Func<BrowserDragData> data)
        {
            PointerPressedEventArgs pressed = null;
            Point origin = default;
            row.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
            {
                if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { pressed = e; origin = e.GetPosition(row); }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            row.AddHandler(InputElement.PointerReleasedEvent, (s, e) => pressed = null, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            row.AddHandler(InputElement.PointerMovedEvent, async (s, e) =>
            {
                if (pressed == null || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                var d = e.GetPosition(row) - origin;
                if (Math.Abs(d.X) < Threshold && Math.Abs(d.Y) < Threshold) return;
                var start = pressed;
                pressed = null;
                try { await DragDrop.DoDragDropAsync(start, Package(data()), DragDropEffects.Copy); }
                catch (Exception) { /* a drag the platform refused is not worth a failed click */ }
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }
}
