using BuzzGUI.Interfaces;
using System;
using System.Windows;

namespace BuzzGUI.Common
{
    /// <summary>
    /// Fork addition (docs/specs/session-view.md 0.3): greys out a sequence editor track while
    /// the Session View has taken it over (the arrangement is not playing on it), as Live does.
    /// Used by the sequence editors' track controls.
    /// </summary>
    public sealed class SessionOverrideIndicator : IDisposable
    {
        const double OverriddenOpacity = 0.35;

        readonly UIElement element;
        readonly ISequence sequence;
        readonly ISession session;

        SessionOverrideIndicator(UIElement element, ISequence sequence, ISession session)
        {
            this.element = element;
            this.sequence = sequence;
            this.session = session;
            session.TrackStateChanged += OnTrackStateChanged;
            session.PropertyChanged += OnSessionPropertyChanged;
            Update();
        }

        /// <summary>
        /// Starts following <paramref name="sequence"/> on <paramref name="element"/>. Returns
        /// null (and leaves the element alone) when there is no session engine.
        /// </summary>
        public static SessionOverrideIndicator Attach(UIElement element, ISequence sequence)
        {
            if (element == null || sequence == null) return null;
            var session = (sequence.Machine?.Graph?.Buzz as ISessionHost)?.Session;
            return session == null ? null : new SessionOverrideIndicator(element, sequence, session);
        }

        void OnTrackStateChanged(ISequence track)
        {
            if (track == sequence) Update();
        }

        void OnSessionPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISession.IsAnyArrangementOverridden)) Update();
        }

        void Update()
        {
            if (!element.Dispatcher.CheckAccess())
            {
                element.Dispatcher.BeginInvoke(new Action(Update));
                return;
            }
            bool overridden = session.IsArrangementOverridden(sequence);
            element.Opacity = overridden ? OverriddenOpacity : 1.0;
        }

        public void Dispose()
        {
            session.TrackStateChanged -= OnTrackStateChanged;
            session.PropertyChanged -= OnSessionPropertyChanged;
            element.Opacity = 1.0;
        }
    }
}
