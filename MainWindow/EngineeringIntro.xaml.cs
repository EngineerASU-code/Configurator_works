using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media;


namespace Configurator;

public partial class EngineeringIntro : UserControl
{
    private Action onFinished;

    public EngineeringIntro() => InitializeComponent();

    public void Play(Action completed)
    {
        onFinished = completed;
        var duration = TimeSpan.FromSeconds(15);
        var count = Stations.Children.Count;
        var blend = TimeSpan.FromMilliseconds(300);
        const double stationSpacing = 520;
        var travel = new DoubleAnimationUsingKeyFrames { Duration = duration };
        travel.KeyFrames.Add(new LinearDoubleKeyFrame(48, TimeSpan.Zero));
        for (int i = 0; i < count; i++)
        {
            var start = duration * i / count;
            var end = duration * (i + 1) / count;
            // Align each illustration with its fixed caption halfway through its time slot.
            travel.KeyFrames.Add(new SplineDoubleKeyFrame(-stationSpacing * i,
                start + (end - start) / 2, new KeySpline(.34, .19, .66, .81)));
            var reveal = new DoubleAnimationUsingKeyFrames { Duration = duration };
            if (i > 0)
            {
                reveal.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, start - blend));
                reveal.KeyFrames.Add(new LinearDoubleKeyFrame(1, start));
            }
            if (i < count - 1)
            {
                reveal.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, end - blend));
                reveal.KeyFrames.Add(new LinearDoubleKeyFrame(0, end));
            }
            else
            {
                reveal.Completed += (_, _) => Finish();
            }
            Stations.Children[i].BeginAnimation(OpacityProperty, reveal);
        }
        travel.KeyFrames.Add(new LinearDoubleKeyFrame(-stationSpacing * (count - 1) - 48, duration));
        Camera.BeginAnimation(TranslateTransform.XProperty, travel);
    }

    private void Skip(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Finish();
    }

    private void Finish()
    {
        var completed = onFinished;
        if (completed == null) return;
        onFinished = null;
        Camera.BeginAnimation(TranslateTransform.XProperty, null);
        foreach (UIElement station in Stations.Children)
            station.BeginAnimation(OpacityProperty, null);
        completed();
    }
}
