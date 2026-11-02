using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NowBar.App.Controls;

namespace NowBar.Tests;

/// <summary>모든 얼굴 표정이 예외 없이 그려지고 눈이 실제로 보이는지 (화면 밖 렌더링).</summary>
public sealed class FaceRenderTests
{
    public static IEnumerable<object[]> Moods() => Enum.GetValues<FaceMood>().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Moods))]
    public void Every_mood_renders_visible_eyes(FaceMood mood)
    {
        int lit = 0;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var face = new FaceView { Mood = mood };
                var host = new System.Windows.Controls.Border { Background = Brushes.Black, Child = face, Width = 72, Height = 32 };
                host.Measure(new Size(72, 32));
                host.Arrange(new Rect(0, 0, 72, 32));
                host.UpdateLayout();

                var bmp = new RenderTargetBitmap(72 * 2, 32 * 2, 192, 192, PixelFormats.Pbgra32);
                bmp.Render(host);
                var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
                bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 150) lit++;

                // 확인용 이미지 저장 (테스트 데이터 폴더)
                var dir = Path.Combine(Path.GetTempPath(), "nowbar-face-renders");
                Directory.CreateDirectory(dir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, $"{mood}.png"));
                encoder.Save(fs);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.True(lit > 20, $"{mood}: 눈이 거의 그려지지 않았습니다 ({lit}px)");
    }
}
