using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class SsdpServerIpPlugin
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    internal const string SearchMessage = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\nUSER-AGENT: RainmeterDesktopWidgets/2.0 UPnP/1.1\r\n\r\n";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8;
        if(args.Length>0&&args[0]=="--scan-child")return RunScanChild();
        string requestId = "";
        try
        {
            Dictionary<string,object> request = Obj(Json.DeserializeObject(Console.In.ReadLine() ?? ""));
            requestId = Str(request,"request_id",""); string action = Str(request,"action","");
            Dictionary<string,object> config = Obj(Get(request,"config"));
            if(action=="configure_discovery") return SsdpServerIpSetupForm.Run(requestId,config);
            if(action=="validate_settings") { ValidateNetwork(config); return Result(requestId,true,new Dictionary<string,object>{{"message","扫描范围有效"}},null); }
            if(action!="get_values") return Result(requestId,false,null,"不支持的 action");
            ValidateNetwork(config);
            string usn=Str(config,"selected_usn","").Trim(); if(usn=="")throw new InvalidDataException("请先搜索并选择 SSDP 服务器");
            int probeTimeout=Clamp(Int(config,"probe_timeout_ms",800),200,5000),scanWait=Clamp(Int(config,"scan_wait_ms",1500),300,10000);
            SsdpDevice found=null;string last=Str(config,"last_ip","").Trim();IPAddress parsed;
            if(IPAddress.TryParse(last,out parsed)&&parsed.AddressFamily==AddressFamily.InterNetwork)found=SsdpScanner.Probe(last,usn,probeTimeout);
            if(found==null)
            {
                NetworkRange range=NetworkRange.Parse(Str(config,"scan_ip",""),Str(config,"subnet_mask",""));
                found=SsdpScanner.Scan(range,usn,scanWait).FirstOrDefault();
                if(found==null)throw new IOException("未在 "+range.Display+" 找到已选择的 USN；服务器可能已关机、故障或不在当前扫描范围。");
            }
            int ttl=Clamp(Int(config,"ttl",60),30,86400);
            Dictionary<string,object> payload=new Dictionary<string,object>{
                {"values",new Dictionary<string,object>{{"server_ip",found.Ip},{"server",found.Server},{"usn",found.Usn},{"location",found.Location}}},
                {"ttl",ttl},{"summary","已找到 SSDP 服务器："+found.Ip},
                {"config_updates",new Dictionary<string,object>{{"previous_ip",last!=""&&last!=found.Ip?last:Str(config,"previous_ip","")},{"last_ip",found.Ip},{"selected_server",found.Server}}}
            };
            return Result(requestId,true,payload,null);
        }
        catch(Exception ex){return Result(requestId,false,null,ex.Message);}
    }

    private static int RunScanChild()
    {
        try
        {
            Dictionary<string,object> config=Obj(Json.DeserializeObject(Console.In.ReadLine()??""));
            NetworkRange range=NetworkRange.Parse(Str(config,"scan_ip",""),Str(config,"subnet_mask",""));
            int wait=Clamp(Int(config,"scan_wait_ms",1500),300,10000);
            List<SsdpDevice> devices=SsdpScanner.Scan(range,"",wait,delegate(int current,int total)
            {
                Console.Out.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","progress"},{"current",current},{"total",total},{"message",current>=total?"已发送全部探测，正在等待应答":"正在扫描 "+current+" / "+total}}));
                Console.Out.Flush();
            });
            Console.Out.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","result"},{"ok",true},{"devices",devices.Select(DeviceObject).Cast<object>().ToArray()}}));
            return 0;
        }
        catch(Exception ex){Console.Out.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","result"},{"ok",false},{"error",ex.Message},{"devices",new object[0]}}));return 1;}
    }
    private static Dictionary<string,object> DeviceObject(SsdpDevice d){return new Dictionary<string,object>{{"ip",d.Ip},{"server",d.Server},{"usn",d.Usn},{"location",d.Location},{"st",d.St}};}
    internal static void ValidateNetwork(Dictionary<string,object> config){NetworkRange.Parse(Str(config,"scan_ip",""),Str(config,"subnet_mask",""));}
    internal static int Result(string id,bool ok,object payload,string error){Console.Out.WriteLine(Json.Serialize(new Dictionary<string,object>{{"type","result"},{"request_id",id},{"ok",ok},{"payload",payload??new Dictionary<string,object>()},{"error",error??""}}));return ok?0:1;}
    internal static object Get(Dictionary<string,object> value,string key){object result;return value!=null&&value.TryGetValue(key,out result)?result:null;}
    internal static Dictionary<string,object> Obj(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
    internal static string Str(Dictionary<string,object> value,string key,string fallback){object result=Get(value,key);return result==null?fallback:Convert.ToString(result,CultureInfo.InvariantCulture);}
    internal static int Int(Dictionary<string,object> value,string key,int fallback){int result;return Int32.TryParse(Str(value,key,""),NumberStyles.Integer,CultureInfo.InvariantCulture,out result)?result:fallback;}
    internal static int Clamp(int value,int min,int max){return Math.Max(min,Math.Min(max,value));}
}

internal sealed class SsdpDevice
{
    public string Ip="",Server="",Usn="",Location="",St="";
    public string SystemName()
    {
        string lower=(Server??"").ToLowerInvariant();int pipe=Server.IndexOf('|');string system=pipe>=0?Server.Substring(pipe+1).Trim():Server.Trim();
        if(lower.Contains("istoreos"))return "iStoreOS";if(lower.Contains("nanopi-r2s"))return "NanoPi R2S";if(lower.StartsWith("microsoft-windows"))return "Windows UPnP";if(lower.StartsWith("go upnp"))return "Go UPnP";
        int slash=system.IndexOf('/');if(slash>0)system=system.Substring(0,slash);return system==""?"未知系统":system;
    }
    public string ShortId()
    {
        string value=(Usn??"").Trim(),lower=value.ToLowerInvariant();int unique=lower.IndexOf("unique:");if(unique>=0)value=value.Substring(unique+7);else{int uuid=lower.IndexOf("uuid:");if(uuid>=0)value=value.Substring(uuid+5);}int suffix=value.IndexOf("::",StringComparison.Ordinal);if(suffix>=0)value=value.Substring(0,suffix);return value.Length>16?value.Substring(0,16):value;
    }
    public override string ToString(){return Ip+"  ·  "+SystemName()+"  ·  "+ShortId();}
}

internal sealed class NetworkRange
{
    public uint First,Last; public string Display;
    public int Count { get { return checked((int)(Last-First+1)); } }
    public static NetworkRange Parse(string addressText,string maskText)
    {
        IPAddress address,mask;
        if(!IPAddress.TryParse((addressText??"").Trim(),out address)||address.AddressFamily!=AddressFamily.InterNetwork)throw new InvalidDataException("扫描 IP 必须是有效 IPv4 地址");
        if(!IPAddress.TryParse((maskText??"").Trim(),out mask)||mask.AddressFamily!=AddressFamily.InterNetwork)throw new InvalidDataException("子网掩码必须是有效 IPv4 掩码");
        uint ip=ToUInt(address),m=ToUInt(mask);bool zeroSeen=false;for(int bit=31;bit>=0;bit--){bool one=(m&(1u<<bit))!=0;if(!one)zeroSeen=true;else if(zeroSeen)throw new InvalidDataException("子网掩码必须连续");}
        uint network=ip&m,broadcast=network|~m;ulong count=(ulong)broadcast-network+1;
        if(count<4)throw new InvalidDataException("扫描范围至少需要 4 个地址");
        if(count>65536)throw new InvalidDataException("首版最多扫描 65536 个地址（最小 /16）");
        return new NetworkRange{First=network+1,Last=broadcast-1,Display=FromUInt(network)+" / "+maskText.Trim()};
    }
    internal static uint ToUInt(IPAddress ip){byte[] b=ip.GetAddressBytes();return ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3];}
    internal static string FromUInt(uint value){return String.Format(CultureInfo.InvariantCulture,"{0}.{1}.{2}.{3}",(value>>24)&255,(value>>16)&255,(value>>8)&255,value&255);}
}

internal static class SsdpScanner
{
    public static SsdpDevice Probe(string ip,string expectedUsn,int timeoutMs){return Run(new[]{ip},expectedUsn,timeoutMs,true,1,null).FirstOrDefault();}
    public static List<SsdpDevice> Scan(NetworkRange range,string expectedUsn,int waitMs){return Scan(range,expectedUsn,waitMs,null);}
    public static List<SsdpDevice> Scan(NetworkRange range,string expectedUsn,int waitMs,Action<int,int> progress){return Run(Enumerate(range),expectedUsn,waitMs,expectedUsn!="",range.Count,progress);}
    private static IEnumerable<string> Enumerate(NetworkRange range){for(uint value=range.First;;value++){yield return NetworkRange.FromUInt(value);if(value==range.Last)break;}}
    private static List<SsdpDevice> Run(IEnumerable<string> targets,string expectedUsn,int waitMs,bool stopOnMatch,int total,Action<int,int> progress)
    {
        List<SsdpDevice> found=new List<SsdpDevice>();object gate=new object();
        using(ManualResetEvent stop=new ManualResetEvent(false))
        using(UdpClient client=new UdpClient(new IPEndPoint(IPAddress.Any,0)))
        {
            byte[] bytes=Encoding.ASCII.GetBytes(SsdpServerIpPlugin.SearchMessage);client.Client.ReceiveTimeout=100;
            Thread receiver=new Thread(delegate(){while(!stop.WaitOne(0)){try{IPEndPoint ep=new IPEndPoint(IPAddress.Any,0);byte[] response=client.Receive(ref ep);SsdpDevice device=Parse(response,ep);if(device==null)continue;bool match=expectedUsn==""||String.Equals(device.Usn,expectedUsn,StringComparison.OrdinalIgnoreCase);lock(gate){if(!found.Any(x=>String.Equals(x.Usn,device.Usn,StringComparison.OrdinalIgnoreCase)&&x.Ip==device.Ip))found.Add(device);if(stopOnMatch&&match)stop.Set();}}catch(SocketException ex){if(ex.SocketErrorCode!=SocketError.TimedOut&&!stop.WaitOne(0))Thread.Sleep(10);}catch(ObjectDisposedException){return;}}});
            receiver.IsBackground=true;receiver.Start();int sent=0;
            foreach(string target in targets){if(stop.WaitOne(0))break;try{client.Send(bytes,bytes.Length,target,1900);}catch(SocketException){}sent++;if(progress!=null&&((sent&255)==0||sent==total))progress(sent,total);if((sent&127)==0)Thread.Sleep(1);}
            if(progress!=null)progress(Math.Min(sent,total),total);stop.WaitOne(waitMs);stop.Set();try{client.Close();}catch{}receiver.Join(500);
        }
        lock(gate)return expectedUsn==""?found.OrderBy(x=>NetworkRange.ToUInt(IPAddress.Parse(x.Ip))).ThenBy(x=>x.SystemName()).ToList():found.Where(x=>String.Equals(x.Usn,expectedUsn,StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
    }
    private static SsdpDevice Parse(byte[] data,IPEndPoint endpoint)
    {
        string text=Encoding.ASCII.GetString(data??new byte[0]);Dictionary<string,string> headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string line in text.Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries)){int colon=line.IndexOf(':');if(colon>0)headers[line.Substring(0,colon).Trim()]=line.Substring(colon+1).Trim();}
        string usn=Value(headers,"USN"),server=Value(headers,"SERVER");if(usn==""||server=="")return null;
        return new SsdpDevice{Ip=endpoint.Address.ToString(),Usn=usn,Server=server,Location=Value(headers,"LOCATION"),St=Value(headers,"ST")};
    }
    private static string Value(Dictionary<string,string> headers,string key){string value;return headers.TryGetValue(key,out value)?value.Trim():"";}
}