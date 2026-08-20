using System;
using Velopack;

namespace ControlTower.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
