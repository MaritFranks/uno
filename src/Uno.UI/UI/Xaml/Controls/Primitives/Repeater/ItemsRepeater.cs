// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Uno;
using Uno.Disposables;
using Uno.UI;
using Uno.UI.Helpers.WinUI;

namespace Microsoft.UI.Xaml.Controls
{
	public partial class ItemsRepeater : FrameworkElement, IPanel
	{
		private readonly UIElementCollection _repeaterChildren;
		UIElementCollection IPanel.Children => _repeaterChildren;
		internal IList<UIElement> Children => _repeaterChildren.AsUIElementList();


		// Change to 'true' to turn on debugging outputs in Output window
		//private static bool s_IsDebugOutputEnabled = false;

		internal static Point ClearedElementsArrangePosition = new Point(-10000.0f, -10000.0f);

		// A convention we use in the ItemsRepeater codebase for an invalid Rect value.
		internal static Rect InvalidRect = new Rect(-1, -1, -1, -1);



		public event TypedEventHandler<ItemsRepeater, ItemsRepeaterElementPreparedEventArgs> ElementPrepared;
		public event TypedEventHandler<ItemsRepeater, ItemsRepeaterElementIndexChangedEventArgs> ElementIndexChanged;
		public event TypedEventHandler<ItemsRepeater, ItemsRepeaterElementClearingEventArgs> ElementClearing;

		// Cached Event args to avoid creation cost every time
		private ItemsRepeaterElementPreparedEventArgs m_elementPreparedArgs;
		private ItemsRepeaterElementClearingEventArgs m_elementClearingArgs;
		private ItemsRepeaterElementIndexChangedEventArgs m_elementIndexChangedArgs;

		//public Microsoft.UI.Xaml.Controls.IElementFactoryShim ItemTemplateShim() { return m_itemTemplateWrapper; };
		internal ViewManager ViewManager => m_viewManager;
		internal AnimationManager AnimationManager => m_animationManager;

		private bool IsProcessingCollectionChange => m_processingItemsSourceChange != null;

		AnimationManager m_animationManager;
		ViewManager m_viewManager ;
		ViewportManager m_viewportManager ;

		ItemsSourceView m_itemsSourceView ;

		Microsoft.UI.Xaml.Controls.IElementFactoryShim m_itemTemplateWrapper ;

		VirtualizingLayoutContext m_layoutContext ;
		object m_layoutState ;
		// Value is different from null only while we are on the OnItemsSourceChanged call stack.
		NotifyCollectionChangedEventArgs m_processingItemsSourceChange ;

		Size m_lastAvailableSize;
		bool m_isLayoutInProgress;
		// The value of _layoutOrigin is expected to be set by the layout
		// when it gets measured. It should not be used outside of measure.
		Point m_layoutOrigin;

		// Loaded events fire on the first tick after an element is put into the tree 
		// while unloaded is posted on the UI tree and may be processed out of sync with subsequent loaded
		// events. We keep these counters to detect out-of-sync unloaded events and take action to rectify.
		int _loadedCounter;
		int _unloadedCounter;

		// Bug in framework's reference tracking causes crash during
		// UIAffinityQueue cleanup. To avoid that bug, take a strong ref
		IElementFactory m_itemTemplate;
		Layout m_layout;
		ElementAnimator m_animator;

		// Bug where DataTemplate with no content causes a crash.
		// See: https://github.com/microsoft/microsoft-ui-xaml/issues/776
		// Solution: Have flag that is only true when DataTemplate exists but it is empty.
		bool m_isItemTemplateEmpty;


		public ItemsRepeater()
		{
			//__RP_Marker_ClassById(RuntimeProfiler.ProfId_ItemsRepeater);

			_repeaterChildren = new UIElementCollection(this);
			m_animationManager = new AnimationManager(this);
			m_viewManager = new ViewManager(this);
			//if (SharedHelpers.IsRS5OrHigher())
			{
				m_viewportManager = new ViewportManagerWithPlatformFeatures(this);
			}
			//else
			//{
			//	m_viewportManager = std.new ViewportManagerDownLevel(this);
			//}

			AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
			//if (SharedHelpers.IsRS3OrHigher())
			{
				TabFocusNavigation = KeyboardNavigationMode.Once;
				XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
			}

			Loaded += OnLoaded;
			Unloaded += OnUnloaded;

			// Initialize the cached layout to the default value
			var layout = Layout as VirtualizingLayout;
			OnLayoutChanged(null, layout);
		}

		~ItemsRepeater()
		{
			m_itemTemplate = null;
			m_animator = null;
			m_layout = null;
		}

		#region IUIElementOverrides

		protected override AutomationPeer OnCreateAutomationPeer()
		{
			return new RepeaterAutomationPeer(this);
		}

		#endregion

		#region IUIElementOverrides7

		protected override IEnumerable<DependencyObject> GetChildrenInTabFocusOrder()
		{
			return CreateChildrenInTabFocusOrderIterable();
		}

		#endregion

		#region IUIElementOverrides8
		protected override void OnBringIntoViewRequested(BringIntoViewRequestedEventArgs e)
		{
			m_viewportManager.OnBringIntoViewRequested(e);
		}

		#endregion

		#region IFrameworkElementOverrides

		protected override Size MeasureOverride(Size availableSize)
		{
			if (m_isLayoutInProgress)
			{
				throw new InvalidOperationException("Reentrancy detected during layout.");
			}

			if (IsProcessingCollectionChange)
			{
				throw new InvalidOperationException("Cannot run layout in the middle of a collection change.");
			}

			m_viewportManager.OnOwnerMeasuring();

			m_isLayoutInProgress = true;
			using var layoutInProgress = Uno.Disposables.Disposable.Create(() => m_isLayoutInProgress = false);

			m_viewManager.PrunePinnedElements();
			Rect extent = default;
			Size desiredSize = default;

			var layout = Layout;
			if (layout != null)
			{
				var layoutContext = GetLayoutContext();

				// Expensive operation, do it only in debug builds.
#if DEBUG && false // TODO UNO
				var virtualContext = (VirtualizingLayoutContext) layoutContext;
				virtualContext.Indent(Indent());
#endif

				// Checking if we have an data template and it is empty
				if (m_isItemTemplateEmpty)
				{
					// Has no content, so we will draw nothing and request zero space
					extent = new Rect(m_layoutOrigin.X, m_layoutOrigin.Y, 0, 0);
				}
				else
				{
					desiredSize = layout.Measure(layoutContext, availableSize);
					extent = new Rect(m_layoutOrigin.X, m_layoutOrigin.Y, desiredSize.Width, desiredSize.Height);
				}

				// Clear var recycle candidate elements that have not been kept alive by layout - i.e layout did not
				// call GetElementAt(index).
				var children = Children;
				for (var i = 0; i < children.Count; ++i)
				{
					var element = children[i];
					var virtInfo = GetVirtualizationInfo(element);

					if (virtInfo.Owner == ElementOwner.Layout &&
						virtInfo.AutoRecycleCandidate &&
						!virtInfo.KeepAlive)
					{
						// TODO UNO REPEATER_TRACE_INFO("AutoClear - %d \n", virtInfo.Index);
						ClearElementImpl(element);
					}
				}
			}

			m_viewportManager.SetLayoutExtent(extent);
			m_lastAvailableSize = availableSize;
			return desiredSize;
		}

		protected  override Size ArrangeOverride(Size finalSize)
		{
			if (m_isLayoutInProgress)
			{
				throw new InvalidOperationException("Reentrancy detected during layout.");
			}

			if (IsProcessingCollectionChange)
			{
				throw new InvalidOperationException("Cannot run layout in the middle of a collection change.");
			}

			m_isLayoutInProgress = true;
			using var layoutInProgress = Uno.Disposables.Disposable.Create(() => m_isLayoutInProgress = false);

			Size arrangeSize = default;
			var layout = Layout;
			if (layout != null)
			{
				arrangeSize = layout.Arrange(GetLayoutContext(), finalSize);
			}

			// The view manager might clear elements during this call.
			// That's why we call it before arranging cleared elements
			// off screen.
			m_viewManager.OnOwnerArranged();

			var children = Children;
			for (var i = 0; i < children.Count; ++i)
			{
				var element = children[i];
				var virtInfo = GetVirtualizationInfo(element);
				virtInfo.KeepAlive = false;

				if (virtInfo.Owner == ElementOwner.ElementFactory ||
					virtInfo.Owner == ElementOwner.PinnedPool)
				{
					// Toss it away. And arrange it with size 0 so that XYFocus won't use it.
					element.Arrange(new Rect(
						ClearedElementsArrangePosition.X - (float)(element.DesiredSize.Width),
						ClearedElementsArrangePosition.Y - (float)(element.DesiredSize.Height),
						0.0,
						0.0));
				}
				else
				{
					var newBounds = CachedVisualTreeHelpers.GetLayoutSlot(element as FrameworkElement);

					if (virtInfo.ArrangeBounds != ItemsRepeater.InvalidRect &&
						newBounds != virtInfo.ArrangeBounds)
					{
						m_animationManager.OnElementBoundsChanged(element, virtInfo.ArrangeBounds, newBounds);
					}

					virtInfo.ArrangeBounds = newBounds;
				}
			}

			m_viewportManager.OnOwnerArranged();
			m_animationManager.OnOwnerArranged();

			return arrangeSize;
		}

		#endregion

		#region IRepeater interface.

		public ItemsSourceView ItemsSourceView => m_itemsSourceView;

		public int GetElementIndex(UIElement element)
		{
			return GetElementIndexImpl(element);
		}

		public UIElement TryGetElement(int index)
		{
			return GetElementFromIndexImpl(index);
		}

		internal void PinElement(UIElement element)
		{
			m_viewManager.UpdatePin(element, true /* addPin */);
		}

		internal void UnpinElement(UIElement element)
		{
			m_viewManager.UpdatePin(element, false /* addPin */);
		}

		UIElement GetOrCreateElement(int index)
		{
			return GetOrCreateElementImpl(index);
		}

		#endregion

		public UIElement GetElementImpl(int index, bool forceCreate, bool suppressAutoRecycle)
		{
			var element = m_viewManager.GetElement(index, forceCreate, suppressAutoRecycle);
			return element;
		}

		public void ClearElementImpl(UIElement element)
		{
			// Clearing an element due to a collection change
			// is more strict in that pinned elements will be forcibly
			// unpinned and sent back to the view generator.
			bool isClearedDueToCollectionChange =
				IsProcessingCollectionChange &&
				(m_processingItemsSourceChange.Action == NotifyCollectionChangedAction.Remove ||
					m_processingItemsSourceChange.Action == NotifyCollectionChangedAction.Replace ||
					m_processingItemsSourceChange.Action == NotifyCollectionChangedAction.Reset);

			m_viewManager.ClearElement(element, isClearedDueToCollectionChange);
			m_viewportManager.OnElementCleared(element);
		}

		int GetElementIndexImpl(UIElement element)
		{
			// Verify that element is actually a child of this ItemsRepeater
			var  parent  = VisualTreeHelper.GetParent(element);
			if (parent == this)
			{
				var virtInfo = TryGetVirtualizationInfo(element);
				return m_viewManager.GetElementIndex(virtInfo);
			}

			return -1;
		}

		UIElement GetElementFromIndexImpl(int index)
		{
			UIElement result = null;

			var children = Children;
			for (int i = 0; i < children.Count && result == null; ++i)
			{
				var element = children[i];
				var virtInfo = TryGetVirtualizationInfo(element);
				if (virtInfo != null && virtInfo.IsRealized && virtInfo.Index == index)
				{
					result = element;
				}
			}

			return result;
		}

		UIElement GetOrCreateElementImpl(int index)
		{
			if (index >= 0 && index >= ItemsSourceView.Count)
			{
				throw new ArgumentException(nameof(index), "Argument index is invalid.");
			}

			if (m_isLayoutInProgress)
			{
				throw new InvalidOperationException("GetOrCreateElement invocation is not allowed during layout.");
			}

			var element = GetElementFromIndexImpl(index);
			bool isAnchorOutsideRealizedRange = element == null;

			if (isAnchorOutsideRealizedRange)
			{
				if (Layout == null)
				{
					throw new InvalidOperationException("Cannot make an Anchor when there is no attached layout.");
				}

				element = GetLayoutContext().GetOrCreateElementAt(index);
				element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			}

			m_viewportManager.OnMakeAnchor(element, isAnchorOutsideRealizedRange);
			InvalidateMeasure();

			return element;
		}

		/*static*/
		internal static VirtualizationInfo TryGetVirtualizationInfo(UIElement element)
		{
			var value = element.GetValue(VirtualizationInfoProperty);
			return (VirtualizationInfo)value;
		}

		/*static*/
		internal static VirtualizationInfo GetVirtualizationInfo(UIElement element)
		{
			var result = TryGetVirtualizationInfo(element);

			if (result == null)
			{
				result = CreateAndInitializeVirtualizationInfo(element);
			}

			return result;
		}

		/* static */
		internal static VirtualizationInfo CreateAndInitializeVirtualizationInfo(UIElement element)
		{
			global::System.Diagnostics.Debug.Assert(TryGetVirtualizationInfo(element) == null);
			var result = new VirtualizationInfo();
			element.SetValue(VirtualizationInfoProperty, result);
			return result;
		}

		private void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			var property = args.Property;

			if (property == ItemsSourceProperty)
			{
				if (args.NewValue != args.OldValue)
				{
					var newValue = args.NewValue;
					var newDataSource = newValue as ItemsSourceView;
					if (newValue != null && newDataSource == null)
					{
						newDataSource = new InspectingDataSource(newValue);
					}

					OnDataSourcePropertyChanged(m_itemsSourceView, newDataSource);
				}
			}
			else if (property == ItemTemplateProperty)
			{
				OnItemTemplateChanged(args.OldValue as IElementFactory, args.NewValue as IElementFactory);
			}
			else if (property == LayoutProperty)
			{
				OnLayoutChanged(args.OldValue as Layout, args.NewValue as Layout);
			}
			else if (property == AnimatorProperty)
			{
				OnAnimatorChanged(args.OldValue as ElementAnimator, args.NewValue as ElementAnimator);
			}
			else if (property == HorizontalCacheLengthProperty)
			{
				m_viewportManager.HorizontalCacheLength = (double)args.NewValue;
			}
			else if (property == VerticalCacheLengthProperty)
			{
				m_viewportManager.VerticalCacheLength = (double)args.NewValue;
			}
		}

		internal void OnElementPrepared(UIElement element, int index)
		{
			m_viewportManager.OnElementPrepared(element);
			var m_elementPreparedEventSource = ElementPrepared;
			if (m_elementPreparedEventSource != null)
			{
				if (m_elementPreparedArgs == null)
				{
					m_elementPreparedArgs = new ItemsRepeaterElementPreparedEventArgs(element, index);
				}
				else
				{
					m_elementPreparedArgs.Update(element, index);
				}

				m_elementPreparedEventSource(this, m_elementPreparedArgs);
			}
		}

		internal void OnElementClearing(UIElement element)
		{
			var m_elementClearingEventSource = ElementClearing;
			if (m_elementClearingEventSource != null)
			{
				if (m_elementClearingArgs == null)
				{
					m_elementClearingArgs = new ItemsRepeaterElementClearingEventArgs(element);
				}
				else
				{
					m_elementClearingArgs.Update(element);
				}

				m_elementClearingEventSource(this, m_elementClearingArgs);
			}
		}

		internal void OnElementIndexChanged(UIElement element, int oldIndex, int newIndex)
		{
			var m_elementIndexChangedEventSource = ElementIndexChanged;
			if (m_elementIndexChangedEventSource != null)
			{
				if (m_elementIndexChangedArgs == null)
				{
					m_elementIndexChangedArgs = new ItemsRepeaterElementIndexChangedEventArgs(element, oldIndex, newIndex);
				}
				else
				{
					m_elementIndexChangedArgs.Update(element, oldIndex, newIndex);
				}

				m_elementIndexChangedEventSource(this, m_elementIndexChangedArgs);
			}
		}

		// Provides an indentation based on repeater elements in the UI Tree that
		// can be used to make logging a little easier to read.
		internal int Indent()
		{
			int indent = 1;

			// Expensive, so we do it only in debug builds.
#if DEBUG
			var parent = this.Parent as FrameworkElement;
			while (parent != null && !(parent is ItemsRepeater))
			{
				parent = parent.Parent as FrameworkElement;
			}

			if (parent is ItemsRepeater parentRepeater)
			{
				indent = parentRepeater.Indent();
			}
#endif

			return indent * 4;
		}

		void OnLoaded(object sender, RoutedEventArgs args)
		{
			// If we skipped an unload event, reset the scrollers now and invalidate measure so that we get a new
			// layout pass during which we will hookup new scrollers.
			if (_loadedCounter > _unloadedCounter)
			{
				InvalidateMeasure();
				m_viewportManager.ResetScrollers();
			}

			++_loadedCounter;
		}

		private void OnUnloaded(object sender, RoutedEventArgs args)
		{
			++_unloadedCounter;
			// Only reset the scrollers if this unload event is in-sync.
			if (_unloadedCounter == _loadedCounter)
			{
				m_viewportManager.ResetScrollers();
			}
		}

		private void OnDataSourcePropertyChanged(ItemsSourceView oldValue, ItemsSourceView newValue)
		{
			if (m_isLayoutInProgress)
			{
				throw new InvalidOperationException("Cannot set ItemsSourceView during layout.");
			}

			m_itemsSourceView = newValue;

			if (oldValue != null)
			{
				newValue.CollectionChanged -= OnItemsSourceViewChanged;
			}

			if (newValue != null)
			{
				newValue.CollectionChanged += OnItemsSourceViewChanged;
			}

			var layout = Layout;
			if (layout != null)
			{
				var args  = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
				using var processingChange = Disposable.Create(() => m_processingItemsSourceChange = null);
				m_processingItemsSourceChange = args;

				if (layout is VirtualizingLayout virtualLayout)
				{
					virtualLayout.OnItemsChangedCore(GetLayoutContext(), newValue, args);
				}
				else if (layout is NonVirtualizingLayout nonVirtualLayout)
				{
					// Walk through all the elements and make sure they are cleared for
					// non-virtualizing layouts.
					foreach (var element in Children)
					{
						if (GetVirtualizationInfo(element).IsRealized)
						{
							ClearElementImpl(element);
						}
					}

					Children.Clear();
				}

				InvalidateMeasure();
			}
		}

		void OnItemTemplateChanged(IElementFactory oldValue, IElementFactory newValue)
		{
			if (m_isLayoutInProgress && oldValue != null)
			{
				throw new InvalidOperationException("ItemTemplate cannot be changed during layout.");
			}

			// Since the ItemTemplate has changed, we need to re-evaluate all the items that
			// have already been created and are now in the tree. The easiest way to do that
			// would be to do a reset.. Note that this has to be done before we change the template
			// so that the cleared elements go back into the old template.
			var layout = Layout;
			if (layout != null)
			{
				var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset);
				using var processingChange = Disposable.Create(() => m_processingItemsSourceChange = null);
				m_processingItemsSourceChange = args;

				if (layout is VirtualizingLayout virtualLayout)
				{
					virtualLayout.OnItemsChangedCore(GetLayoutContext(), newValue, args);
				}
				else if (layout is NonVirtualizingLayout nonVirtualLayout)
				{
					// Walk through all the elements and make sure they are cleared for
					// non-virtualizing layouts.
					foreach (var child in Children)
					{
						if (GetVirtualizationInfo(child).IsRealized)
						{
							ClearElementImpl(child);
						}
					}
				}
			}

			if (!SharedHelpers.IsRS5OrHigher())
			{
				// Bug in framework's reference tracking causes crash during
				// UIAffinityQueue cleanup. To avoid that bug, take a strong ref
				m_itemTemplate = newValue;
			}

			// Clear flag for bug #776
			m_isItemTemplateEmpty = false;
			m_itemTemplateWrapper = newValue as IElementFactoryShim;
			if (m_itemTemplateWrapper == null)
			{
				// ItemTemplate set does not implement IElementFactoryShim. We also 
				// want to support DataTemplate and DataTemplateSelectors automagically.
				if (newValue is DataTemplate dataTemplate)
				{
					m_itemTemplateWrapper = new ItemTemplateWrapper(dataTemplate);
					if (!(dataTemplate.LoadContent() is FrameworkElement))
					{
						// We have a DataTemplate which is empty, so we need to set it to true
						m_isItemTemplateEmpty = true;
					}
				}
				else if (newValue is DataTemplateSelector selector)
				{
					m_itemTemplateWrapper = new ItemTemplateWrapper(selector);
				}
				else
				{
					throw new ArgumentException("ItemTemplate", "ItemTemplate");
				}
			}

			InvalidateMeasure();
		}

		void OnLayoutChanged(Layout oldValue, Layout newValue)
		{
			if (m_isLayoutInProgress)
			{
				throw new InvalidOperationException("Layout cannot be changed during layout.");
			}

			m_viewManager.OnLayoutChanging();
			m_animationManager.OnLayoutChanging();

			if (oldValue != null)
			{
				oldValue.UninitializeForContext(GetLayoutContext());
				newValue.MeasureInvalidated -= InvalidateMeasureForLayout;
				newValue.ArrangeInvalidated -= InvalidateArrangeForLayout;

				// Walk through all the elements and make sure they are cleared
				var children = Children;
				for (int i = 0; i < children.Count; ++i)
				{
					var element = children[i];
					if (GetVirtualizationInfo(element).IsRealized)
					{
						ClearElementImpl(element);
					}
				}

				m_layoutState = null;
			}

			if (!SharedHelpers.IsRS5OrHigher())
			{
				// Bug in framework's reference tracking causes crash during
				// UIAffinityQueue cleanup. To avoid that bug, take a strong ref
				m_layout = newValue;
			}

			if (newValue != null)
			{
				newValue.InitializeForContext(GetLayoutContext());
				newValue.MeasureInvalidated += InvalidateMeasureForLayout;
				newValue.ArrangeInvalidated += InvalidateArrangeForLayout;
			}

			bool isVirtualizingLayout = newValue is VirtualizingLayout;
			m_viewportManager.OnLayoutChanged(isVirtualizingLayout);
			InvalidateMeasure();
		}

		void OnAnimatorChanged(ElementAnimator oldValue, ElementAnimator newValue)
		{
			m_animationManager.OnAnimatorChanged(newValue);
			if (!SharedHelpers.IsRS5OrHigher())
			{
				// Bug in framework's reference tracking causes crash during
				// UIAffinityQueue cleanup. To avoid that bug, take a strong ref
				m_animator = newValue;
			}
		}

		void OnItemsSourceViewChanged(object sender, NotifyCollectionChangedEventArgs args)
		{
			if (m_isLayoutInProgress)
			{
				// Bad things will follow if the data changes while we are in the middle of a layout pass.
				throw new InvalidOperationException("Changes in data source are not allowed during layout.");
			}

			if (IsProcessingCollectionChange)
			{
				throw new InvalidOperationException("Changes in the data source are not allowed during another change in the data source.");
			}

			m_processingItemsSourceChange = args;
			using var processingChange = Disposable.Create(() => m_processingItemsSourceChange = null);

			m_animationManager.OnItemsSourceChanged(sender, args);
			m_viewManager.OnItemsSourceChanged(sender, args);

			var layout = Layout;
			if (layout != null)
			{
				if (layout is VirtualizingLayout virtualLayout)
				{
					virtualLayout.OnItemsChangedCore(GetLayoutContext(), sender, args);
				}
				else
				{
					// NonVirtualizingLayout
					InvalidateMeasure();
				}
			}
		}

		void InvalidateMeasureForLayout(Layout sender, object args)
		{
			InvalidateMeasure();
		}

		void InvalidateArrangeForLayout(Layout sender, object args)
		{
			InvalidateArrange();
		}

		private VirtualizingLayoutContext GetLayoutContext()
		{
			if (m_layoutContext == null)
			{
				m_layoutContext = new RepeaterLayoutContext(this);
			}

			return m_layoutContext;
		}

		IEnumerable<DependencyObject> CreateChildrenInTabFocusOrderIterable()
		{
			var children = Children;
			if (children.Count > 0u)
			{
				return new ChildrenInTabFocusOrderIterable(this);
			}

			return null;
		}
	}
}
