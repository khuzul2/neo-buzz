using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;

namespace ReBuzz.Studio.Tests
{
    public class ShellTests
    {
        [AvaloniaTest]
        public void AWindowRendersHeadless()
        {
            var window = new Window { Title = "ReBuzz", Width = 640, Height = 400, Content = new TextBlock { Text = "ReBuzz" } };
            window.Show();
            var frame = window.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.PixelSize.Width, Is.EqualTo(640));
        }
    }
}
