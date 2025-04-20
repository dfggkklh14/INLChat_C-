using Microsoft.Extensions.Configuration;
using System;

namespace Server
{
    class Program
    {
        static void Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var server = new Server(configuration);
            server.StartServer();
        }
    }
}