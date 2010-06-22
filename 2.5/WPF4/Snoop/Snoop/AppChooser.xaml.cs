// (c) Copyright Microsoft Corporation.
// This source is subject to the Microsoft Public License (Ms-PL).
// Please see http://go.microsoft.com/fwlink/?LinkID=131993 for details.
// All other rights reserved.

using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ManagedInjector;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Snoop
{
	public partial class AppChooser
	{
		static AppChooser()
		{
			AppChooser.RefreshCommand.InputGestures.Add(new KeyGesture(Key.F5));
		}

		public AppChooser()
		{
			this.windowsView = CollectionViewSource.GetDefaultView(this.windows);

			this.InitializeComponent();

			this.CommandBindings.Add(new CommandBinding(AppChooser.RefreshCommand, this.HandleRefreshCommand));
			this.CommandBindings.Add(new CommandBinding(AppChooser.InspectCommand, this.HandleInspectCommand, this.HandleCanInspectOrMagnifyCommand));
			this.CommandBindings.Add(new CommandBinding(AppChooser.MagnifyCommand, this.HandleMagnifyCommand, this.HandleCanInspectOrMagnifyCommand));

#if X86
			this.snoopTextBlock.Text = "(32-bit)";
#endif
#if X64
			this.snoopTextBlock.Text = "(64-bit)";
#endif

			AutoRefresh = false;
			DispatcherTimer timer =
				new DispatcherTimer
				(
					TimeSpan.FromSeconds(20),
					DispatcherPriority.Background,
					this.HandleRefreshTimer,
					Dispatcher.CurrentDispatcher
				);
			this.Refresh();
		}


		public static readonly RoutedCommand InspectCommand = new RoutedCommand();
		public static readonly RoutedCommand RefreshCommand = new RoutedCommand();
		public static readonly RoutedCommand MagnifyCommand = new RoutedCommand();


		public ICollectionView Windows
		{
			get { return this.windowsView; }
		}
		private ICollectionView windowsView;
		private ObservableCollection<WindowInfo> windows = new ObservableCollection<WindowInfo>();

		public bool AutoRefresh { get; set; }

		public void Refresh()
		{
			this.windows.Clear();

			Dispatcher.BeginInvoke
			(
				System.Windows.Threading.DispatcherPriority.Loaded,
				(DispatcherOperationCallback)delegate
				{
					try
					{
						Mouse.OverrideCursor = Cursors.Wait;

						foreach (IntPtr windowHandle in NativeMethods.ToplevelWindows)
						{
							WindowInfo window = new WindowInfo(windowHandle, this);
							if (window.IsValidProcess && !this.HasProcess(window.OwningProcess))
								this.windows.Add(window);
						}

						if (this.windows.Count > 0)
							this.windowsView.MoveCurrentTo(this.windows[0]);
					}
					finally
					{
						Mouse.OverrideCursor = null;
					}
					return null;
				},
				null
			);
		}


		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			try
			{
				// load the window placement details from the user settings.
				WINDOWPLACEMENT wp = (WINDOWPLACEMENT)Properties.Settings.Default.AppChooserWindowPlacement;
				wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
				wp.flags = 0;
				wp.showCmd = (wp.showCmd == Win32.SW_SHOWMINIMIZED ? Win32.SW_SHOWNORMAL : wp.showCmd);
				IntPtr hwnd = new WindowInteropHelper(this).Handle;
				Win32.SetWindowPlacement(hwnd, ref wp);
			}
			catch
			{
			}
		}
		protected override void OnClosing(CancelEventArgs e)
		{
			base.OnClosing(e);

			// persist the window placement details to the user settings.
			WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			Win32.GetWindowPlacement(hwnd, out wp);
			Properties.Settings.Default.AppChooserWindowPlacement = wp;
			Properties.Settings.Default.Save();
		}


		private bool HasProcess(Process process)
		{
			foreach (WindowInfo window in this.windows)
				if (window.OwningProcess.Id == process.Id)
					return true;
			return false;
		}

		private void HandleCanInspectOrMagnifyCommand(object sender, CanExecuteRoutedEventArgs e)
		{
			if (this.windowsView.CurrentItem != null)
				e.CanExecute = true;
			e.Handled = true;
		}
		private void HandleInspectCommand(object sender, ExecutedRoutedEventArgs e)
		{
			WindowInfo window = (WindowInfo)this.windowsView.CurrentItem;
			if (window != null)
				window.Snoop();
		}
		private void HandleMagnifyCommand(object sender, ExecutedRoutedEventArgs e)
		{
			WindowInfo window = (WindowInfo)this.windowsView.CurrentItem;
			if (window != null)
				window.Magnify();
		}
		private void HandleRefreshCommand(object sender, ExecutedRoutedEventArgs e)
		{
			// clear out cached process info to make the force refresh do the process check over again.
			WindowInfo.ClearCachedProcessInfo();
			this.Refresh();
		}

		private void HandleRefreshTimer(object sender, EventArgs e)
		{
			if (AutoRefresh)
			{
				this.Refresh();
			}
		}
		private void HandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			this.DragMove();
		}
		private void HandleMinimize(object sender, MouseButtonEventArgs e)
		{
			this.WindowState = System.Windows.WindowState.Minimized;
		}
		private void HandleClose(object sender, MouseButtonEventArgs e)
		{
			this.Close();
		}
	}

	public class WindowInfo
	{
		public WindowInfo(IntPtr hwnd, AppChooser appChooser)
		{
			this.hwnd = hwnd;
			this.appChooser = appChooser;
		}


		public bool IsValidProcess
		{
			get
			{
				bool isValid = false;
				try
				{
					if (this.hwnd == IntPtr.Zero)
						return false;

					Process process = this.OwningProcess;
					if (process == null)
						return false;

					// see if we have cached the process validity previously, if so, return it.
					if (WindowInfo.processIDToValidityMap.TryGetValue(process.Id, out isValid))
						return isValid;

					// else determine the process validity and cache it.
					if (process.Id == Process.GetCurrentProcess().Id)
					{
						isValid = false;

						// the above line stops the user from snooping on snoop, since we assume that ... that isn't their goal.
						// to get around this, the user can bring up two snoops and use the second snoop ... to snoop the first snoop.
						// well, that let's you snoop the app chooser. in order to snoop the main snoop ui, you have to bring up three snoops.
						// in this case, bring up two snoops, as before, and then bring up the third snoop, using it to snoop the first snoop.
						// since the second snoop inserted itself into the first snoop's process, you can now spy the main snoop ui from the
						// second snoop (bring up another main snoop ui to do so). pretty tricky, huh! and useful!
					}
					else
					{
						// a process is valid to snoop if it contains a dependency on PresentationFramework.dll
						foreach (ProcessModule module in process.Modules)
						{
							if
							(
								module.ModuleName.Contains("PresentationFramework.dll") ||
								module.ModuleName.Contains("PresentationFramework.ni.dll")
							)
							{
								isValid = true;
								break;
							}
						}
					}

					WindowInfo.processIDToValidityMap[process.Id] = isValid;
				}
				catch(Exception) {}
				return isValid;
			}
		}
		public Process OwningProcess
		{
			get { return NativeMethods.GetWindowThreadProcess(this.hwnd); }
		}
		public IntPtr HWnd
		{
			get { return this.hwnd; }
		}
		private IntPtr hwnd;
		public string Description
		{
			get
			{
				Process process = this.OwningProcess;
				return process.MainWindowTitle + " - " + process.ProcessName + " [" + process.Id.ToString() + "]";
			}
		}
		public override string ToString()
		{
			return this.Description;
		}


		public static void ClearCachedProcessInfo()
		{
			WindowInfo.processIDToValidityMap.Clear();
		}
		public void Snoop()
		{
			Mouse.OverrideCursor = Cursors.Wait;
			try
			{
				Injector.Launch(this.HWnd, typeof(SnoopUI).Assembly, typeof(SnoopUI).FullName, "GoBabyGo");
			}
			catch (Exception)
			{
				if (this.appChooser != null)
					this.appChooser.Refresh();
			}
			Mouse.OverrideCursor = null;
		}
		public void Magnify()
		{
			Mouse.OverrideCursor = Cursors.Wait;
			try
			{
				Injector.Launch(this.HWnd, typeof(Zoomer).Assembly, typeof(Zoomer).FullName, "GoBabyGo");
			}
			catch (Exception)
			{
				if (this.appChooser != null)
					this.appChooser.Refresh();
			}
			Mouse.OverrideCursor = null;
		}


		private AppChooser appChooser;
		private static Dictionary<int, bool> processIDToValidityMap = new Dictionary<int, bool>();
	}
}