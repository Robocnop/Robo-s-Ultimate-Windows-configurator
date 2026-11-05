using System;
using System.Collections.Generic;
using System.Diagnostics;
using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class SystemInfoTests
    {
        [Fact]
        public void Run_ReturnsCodeAndOutput()
        {
            var lines = new List<string>();
            int code = SystemInfo.Run("cmd.exe", "/c echo bonjour & exit 3", lines.Add, timeout: TimeSpan.FromSeconds(30));
            Assert.Equal(3, code);
            Assert.Contains("bonjour", lines);
        }

        [Fact]
        public void Run_StopsAProgramThatTakesTooLong()
        {
            var watch = Stopwatch.StartNew();
            // ping lancé par cmd : taskkill /T doit arrêter les deux
            Assert.Throws<TimeoutException>(() => SystemInfo.Run("cmd.exe", "/c ping -n 60 127.0.0.1 > nul", timeout: TimeSpan.FromSeconds(1)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"arrêté en {watch.Elapsed}");
        }
    }
}
