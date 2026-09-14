using System;
using RainmeterBackend;

internal static class AddressProviderProbe
{
    private static int Main(string[] args)
    {
        if(args.Length!=4)return 2;
        AddressProviderBinding provider=DynamicPluginValues.AddressProvider(args[0]);
        if(args[1]=="none")return provider==null?0:3;
        if(provider==null||provider.PluginId!=args[1]||provider.Priority!=Int32.Parse(args[2]))return 4;
        string actual=DynamicPluginValues.BindForTarget("http://198.51.100.20:9800/dav/path",args[0]);
        string expected="http://"+args[3]+":9800/dav/path";
        if(!String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))return 5;
        Console.WriteLine(provider.PluginId+" -> "+actual);
        return 0;
    }
}
