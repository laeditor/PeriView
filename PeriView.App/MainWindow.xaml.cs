using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using PeriView.App.ViewModels;

namespace PeriView.App;

public partial class MainWindow : Window
{
    private static readonly WpfBrush DragIndicatorBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x64, 0xD8));
    private static readonly WpfPen DragIndicatorPen = new(DragIndicatorBrush, 2);

    private WpfPoint _dragStartPoint;
    private DeviceStatusItemViewModel? _dragSourceItem;
    private DataGridRow? _dragTargetRow;
    private AdornerLayer? _dragInsertionLayer;
    private RowInsertionAdorner? _dragInsertionAdorner;
    private bool _insertAfterTarget;

    static MainWindow()
    {
        if (DragIndicatorBrush.CanFreeze)
        {
            DragIndicatorBrush.Freeze();
        }

        if (DragIndicatorPen.CanFreeze)
        {
            DragIndicatorPen.Freeze();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void DevicesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        _dragSourceItem = ResolveItemFromOriginalSource(e.OriginalSource);
    }

    private void DevicesGrid_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSourceItem is null)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        try
        {
            DragDrop.DoDragDrop(DevicesGrid, _dragSourceItem, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            _dragSourceItem = null;
            ClearDragInsertionIndicator();
        }
    }

    private void DevicesGrid_DragOver(object sender, WpfDragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(DeviceStatusItemViewModel)))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            ClearDragInsertionIndicator();
            e.Handled = true;
            return;
        }

        var targetRow = ResolveRowFromOriginalSource(e.OriginalSource);
        if (targetRow is null)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            ClearDragInsertionIndicator();
            e.Handled = true;
            return;
        }

        var pointer = e.GetPosition(targetRow);
        var insertAfter = pointer.Y >= targetRow.ActualHeight / 2;
        UpdateDragInsertionIndicator(targetRow, insertAfter);

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void DevicesGrid_DragLeave(object sender, WpfDragEventArgs e)
    {
        if (!DevicesGrid.IsMouseOver)
        {
            ClearDragInsertionIndicator();
        }
    }

    private void DevicesGrid_Drop(object sender, WpfDragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(typeof(DeviceStatusItemViewModel)))
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var source = (DeviceStatusItemViewModel)e.Data.GetData(typeof(DeviceStatusItemViewModel))!;
            var target = ResolveItemFromOriginalSource(e.OriginalSource);

            viewModel.MoveDevice(source, target, _insertAfterTarget);
        }
        finally
        {
            ClearDragInsertionIndicator();
        }
    }

    private static DeviceStatusItemViewModel? ResolveItemFromOriginalSource(object originalSource)
    {
        var row = ResolveRowFromOriginalSource(originalSource);
        return row?.Item as DeviceStatusItemViewModel;
    }

    private static DataGridRow? ResolveRowFromOriginalSource(object originalSource)
    {
        if (originalSource is not DependencyObject dependencyObject)
        {
            return null;
        }

        return FindParent<DataGridRow>(dependencyObject);
    }

    private void UpdateDragInsertionIndicator(DataGridRow row, bool insertAfter)
    {
        if (_dragTargetRow != row)
        {
            ClearDragInsertionIndicator();
            _dragTargetRow = row;
            _dragInsertionLayer = AdornerLayer.GetAdornerLayer(row);

            if (_dragInsertionLayer is not null)
            {
                _dragInsertionAdorner = new RowInsertionAdorner(row, insertAfter);
                _dragInsertionLayer.Add(_dragInsertionAdorner);
            }
        }

        if (_dragInsertionAdorner is not null)
        {
            _dragInsertionAdorner.UpdateInsertAfter(insertAfter);
        }

        _insertAfterTarget = insertAfter;
    }

    private void ClearDragInsertionIndicator()
    {
        if (_dragInsertionLayer is not null && _dragInsertionAdorner is not null)
        {
            _dragInsertionLayer.Remove(_dragInsertionAdorner);
        }

        _dragInsertionAdorner = null;
        _dragInsertionLayer = null;
        _dragTargetRow = null;
        _insertAfterTarget = false;
    }

    private sealed class RowInsertionAdorner : Adorner
    {
        private bool _insertAfter;

        public RowInsertionAdorner(UIElement adornedElement, bool insertAfter)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            _insertAfter = insertAfter;
            SnapsToDevicePixels = true;
        }

        public void UpdateInsertAfter(bool insertAfter)
        {
            if (_insertAfter == insertAfter)
            {
                return;
            }

            _insertAfter = insertAfter;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var width = AdornedElement.RenderSize.Width;
            if (width <= 0)
            {
                return;
            }

            var y = _insertAfter
                ? Math.Max(1, AdornedElement.RenderSize.Height - 1)
                : 1;
            drawingContext.DrawLine(DragIndicatorPen, new WpfPoint(2, y), new WpfPoint(Math.Max(2, width - 2), y));
        }
    }

    private static T? FindParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var parent = child;
        while (parent is not null)
        {
            if (parent is T typed)
            {
                return typed;
            }

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }
}
