using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Docking;
using Floss.App.Document;
using Floss.App.Features;

namespace Floss.App.Features.Dock;

/// <summary>
/// Undo history docker — timeline list bound to <see cref="IDocumentHistorySource"/>.
/// </summary>
public sealed class UndoHistoryDockFeature : IFeatureModule
{
    public const string PanelId = "history";

    public void Register(IFeatureSession session)
    {
        DockFeature.Register(
            PanelId,
            "Undo History",
            () => new UndoHistoryPanel(session.GetService<IDocumentHistorySource>()),
            defaultZone: "right-0",
            proportion: 0.12,
            minHeight: 96,
            sizing: DockPanelSizing.Fill);
    }

    private sealed class UndoHistoryPanel : Decorator
    {
        private static readonly IBrush SavedMarker = new SolidColorBrush(Color.Parse("#6a9e6a"));
        private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#888888"));

        private readonly IDocumentHistorySource _history;
        private readonly Grid _root;
        private readonly ListBox _list;
        private readonly TextBlock _emptyHint;
        private readonly ObservableCollection<DocumentHistoryEntry> _items = [];
        private bool _syncingSelection;

        public UndoHistoryPanel(IDocumentHistorySource history)
        {
            _history = history;

            _emptyHint = new TextBlock
            {
                Text = "Open or create a document to see undo history.",
                Foreground = MutedText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 10),
                IsVisible = false,
            };

            _list = new ListBox
            {
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2),
                ItemsSource = _items,
                ItemTemplate = new FuncDataTemplate<DocumentHistoryEntry>((entry, _) => BuildItem(entry)),
            };

            _root = new Grid();
            _root.Children.Add(_list);
            _root.Children.Add(_emptyHint);

            Child = _root;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _history.Changed += RebuildList;
            _list.SelectionChanged += OnSelectionChanged;
            RebuildList();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _history.Changed -= RebuildList;
            _list.SelectionChanged -= OnSelectionChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private static Control BuildItem(DocumentHistoryEntry entry)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(2, 1),
            };

            row.Children.Add(new TextBlock
            {
                Text = entry.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (entry.IsSaved)
            {
                var marker = new TextBlock
                {
                    Text = "●",
                    FontSize = 9,
                    Foreground = SavedMarker,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 2, 0),
                };
                Grid.SetColumn(marker, 1);
                row.Children.Add(marker);
            }

            return row;
        }

        private void RebuildList()
        {
            _syncingSelection = true;
            try
            {
                var hasDocument = _history.HasDocument;
                _emptyHint.IsVisible = !hasDocument;
                _list.IsVisible = hasDocument;

                // Clear selection before mutating ItemsSource — otherwise Avalonia's
                // selection model throws when the collection is reset mid-click.
                _list.SelectedIndex = -1;
                _items.Clear();

                if (!hasDocument)
                    return;

                foreach (var entry in _history.Entries)
                    _items.Add(entry);

                var index = Math.Clamp(_history.CurrentIndex, 0, Math.Max(0, _items.Count - 1));
                _list.SelectedIndex = _items.Count == 0 ? -1 : index;

                if (_list.SelectedItem != null)
                    _list.ScrollIntoView(_list.SelectedItem);
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || !_history.HasDocument)
                return;

            // Defer until the ListBox finishes its selection commit; JumpTo fires
            // HistoryChanged → RebuildList which must not run inside this handler.
            Dispatcher.UIThread.Post(() =>
            {
                if (_syncingSelection || !_history.HasDocument)
                    return;

                var index = _list.SelectedIndex;
                if (index < 0 || index >= _history.Entries.Count || index == _history.CurrentIndex)
                    return;

                if (!_history.JumpTo(index))
                    RebuildList();
            }, DispatcherPriority.Background);
        }
    }
}
