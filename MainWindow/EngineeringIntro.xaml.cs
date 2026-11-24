using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Configurator;

public partial class EngineeringIntro : UserControl
{
    public EngineeringIntro()
    {
        InitializeComponent();
        foreach (var name in new[] { "CPU", "DI", "DI", "DO", "AI", "AO" })
        {
            var panel = new StackPanel { Background = Brushes.DarkSlateGray, Margin = new Thickness(2) };
            panel.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, Margin = new Thickness(6) });
            panel.Children.Add(new TextBlock { Text = "●", Foreground = Brushes.LightGreen, Margin = new Thickness(6, 0, 0, 12) });
            for (int i = 0; i < 5; i++)
                panel.Children.Add(new Border { Height = 3, Background = Brushes.SlateGray, Margin = new Thickness(7, 4, 7, 0) });
            Modules.Children.Add(panel);
        }
    }

    public void Play(Action completed)
    {
        var camera = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(7.5) };
        foreach (var (second, offset) in new[] { (0.0, 0.0), (2.3, -24.0), (3.7, -486.0), (4.9, -880.0), (6.0, -1410.0), (7.5, -1440.0) })
            camera.KeyFrames.Add(new SplineDoubleKeyFrame(offset, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(second)), new KeySpline(.32, .1, .58, 1)));
        camera.Completed += (_, _) => completed();
        Camera.BeginAnimation(TranslateTransform.XProperty, camera);
        var draft = TimeSpan.FromSeconds(2.65);
        Trace.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, 0, 80, 480), new Rect(0, 0, 710, 480), draft));
        PenPosition.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(80, 710, draft));
        Cursor.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(.15)) { BeginTime = draft });
        var slide = new TranslateTransform();
        Modules.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-35, 0, TimeSpan.FromSeconds(.65)) { BeginTime = TimeSpan.FromSeconds(5.7), FillBehavior = FillBehavior.HoldEnd });
        Wiring.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, 0, 405, 0), new Rect(0, 0, 405, 64), TimeSpan.FromSeconds(.95)) { BeginTime = TimeSpan.FromSeconds(6.4) });
        Flow.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(64, 0, TimeSpan.FromSeconds(5.6)));
        Needle.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 108, TimeSpan.FromSeconds(1)) { BeginTime = TimeSpan.FromSeconds(4.2) });
    }
}
