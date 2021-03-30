﻿using System;
using Microsoft.UI.Xaml.Controls;

#pragma warning disable CA1416

namespace WinUIComponent
{
    public static class TestButtons
    {
        public static WeakReference weakRefCustomButton = null;
        public static WeakReference weakRefRegularButton = null;

        public static CustomButton GetCustomButton()
        {
            var customButton = new CustomButton();
            weakRefCustomButton = new WeakReference(customButton);
            return customButton;
        }

        public static Button GetRegularButton()
        {
            Button button = new Button
            {
                Content = "Button"
            };
            weakRefRegularButton = new WeakReference(button);
            return button;
        }

        public static bool IsAliveCustomButton()
        {
            GC.Collect(2, GCCollectionMode.Forced);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return (weakRefCustomButton != null && weakRefCustomButton.IsAlive);
        }

        public static bool IsAliveRegularButton()
        {
            GC.Collect(2, GCCollectionMode.Forced);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return (weakRefRegularButton != null && weakRefRegularButton.IsAlive);
        }
    }
}