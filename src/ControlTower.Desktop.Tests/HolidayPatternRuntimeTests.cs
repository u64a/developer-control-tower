using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ControlTower.Desktop.Tests;

public sealed class HolidayPatternRuntimeTests
{
    [Theory]
    [InlineData("Christmas", "#F4F7FA")]
    [InlineData("Halloween", "#1A0A0A")]
    public void Pattern_RepeatsOpaquelyBeyondLargeWindowWidths(string method, string backgroundHex)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var background = (Color)ColorConverter.ConvertFromString(backgroundHex);
                var type = typeof(App).Assembly.GetType("ControlTower.Desktop.Appearance.HolidayPatternBrushes", throwOnError: true)!;
                var factory = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!;
                var brush = Assert.IsType<DrawingBrush>(factory.Invoke(null, new object[] { background }));
                Assert.True(brush.IsFrozen);

                const int width = 4200;
                const int height = 168;
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                var distantMotifPixels = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var offset = (y * width + x) * 4;
                        Assert.Equal((byte)255, pixels[offset + 3]);
                        if (x >= 4000 && (pixels[offset] != background.B ||
                            pixels[offset + 1] != background.G || pixels[offset + 2] != background.R))
                        {
                            distantMotifPixels++;
                        }
                    }
                }
                Assert.True(distantMotifPixels > 0, "Motifs must continue on large windows, not stop at a fixed canvas edge.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Pattern rendering timed out.");
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
