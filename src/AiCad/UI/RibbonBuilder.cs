using System;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AiCad.UI
{
    /// <summary>
    /// Adds an "AiCad" ribbon tab so the plug-in can be driven entirely by
    /// clicking, with no command-line typing.
    /// </summary>
    public static class RibbonBuilder
    {
        private const string TabId = "AICAD_RIBBON_TAB";
        private static bool _hooked;

        /// <summary>
        /// Builds the tab now if the ribbon exists, otherwise waits for it.
        /// The ribbon is often not constructed yet when a plug-in loads.
        /// </summary>
        public static void Install()
        {
            if (ComponentManager.Ribbon != null)
            {
                Build();
                return;
            }

            if (_hooked) return;
            _hooked = true;
            ComponentManager.ItemInitialized += OnItemInitialized;
        }

        private static void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            _hooked = false;
            Build();
        }

        private static void Build()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // Never add the tab twice, e.g. after a re-NETLOAD.
            foreach (RibbonTab existing in ribbon.Tabs)
            {
                if (existing.Id == TabId) return;
            }

            RibbonTab tab = new RibbonTab();
            tab.Title = "AiCad";
            tab.Id = TabId;

            RibbonPanelSource source = new RibbonPanelSource();
            source.Title = "AI assistant";

            source.Items.Add(MakeButton("Assistant", "Open the AI drawing assistant palette", "_.AICAD ", true));
            source.Items.Add(new RibbonSeparator());
            source.Items.Add(MakeButton("Sample", "Draw the built-in sample - no API call", "_.AICADTEST ", false));
            source.Items.Add(MakeButton("Settings", "Provider, API key and house standards", "_.AICADCONFIG ", false));
            source.Items.Add(MakeButton("Operations", "List everything the AI can draw", "_.AICADOPS ", false));

            RibbonPanel panel = new RibbonPanel();
            panel.Source = source;
            tab.Panels.Add(panel);

            ribbon.Tabs.Add(tab);
        }

        private static RibbonButton MakeButton(string text, string tooltip, string command, bool large)
        {
            RibbonButton button = new RibbonButton();
            button.Text = text;
            button.ShowText = true;
            button.ShowImage = false;
            button.Size = large ? RibbonItemSize.Large : RibbonItemSize.Standard;
            button.Orientation = Orientation.Vertical;
            button.ToolTip = tooltip;
            button.CommandParameter = command;
            button.CommandHandler = new RibbonCommandHandler();
            return button;
        }
    }

    /// <summary>
    /// Runs a ribbon button's command in document context. The command string
    /// is a fixed literal from RibbonBuilder, never anything user- or
    /// model-supplied.
    /// </summary>
    public class RibbonCommandHandler : ICommand
    {
        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return true;
        }

        public void Execute(object parameter)
        {
            RibbonButton button = parameter as RibbonButton;
            if (button == null) return;

            string command = button.CommandParameter as string;
            if (string.IsNullOrEmpty(command)) return;

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.SendStringToExecute(command, true, false, true);
        }

        /// <summary>Kept so the compiler does not warn about an unused event.</summary>
        protected virtual void OnCanExecuteChanged()
        {
            EventHandler handler = CanExecuteChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
