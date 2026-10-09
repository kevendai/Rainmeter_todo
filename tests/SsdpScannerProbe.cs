using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal static class SsdpScannerProbe
{
    private static int Main()
    {
        using(UdpClient server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            int port=((IPEndPoint)server.Client.LocalEndPoint).Port;
            server.Client.ReceiveTimeout=3000;
            Exception failure=null;int requests=0;
            Thread responder=new Thread(delegate(){try{
                IPEndPoint sender=new IPEndPoint(IPAddress.Any,0);
                server.Receive(ref sender);Interlocked.Increment(ref requests); // Deliberately drop the first probe.
                server.Receive(ref sender);Interlocked.Increment(ref requests);
                for(int copy=0;copy<2;copy++)for(int i=0;i<128;i++){
                    byte[] data=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nUSN: uuid:test-"+i+"\r\nSERVER: Probe/1.0\r\nLOCATION: http://127.0.0.1/device\r\n\r\n");
                    server.Send(data,data.Length,sender);
                }
            }catch(Exception ex){failure=ex;}});
            responder.IsBackground=true;responder.Start();
            var devices=SsdpScanner.Run(new[]{"127.0.0.1"},"",1000,false,1,null,port);
            if(!responder.Join(3500))return 1;
            if(failure!=null){Console.Error.WriteLine(failure);return 2;}
            if(requests!=2||devices.Count!=128){Console.Error.WriteLine("requests="+requests+", devices="+devices.Count);return 3;}
            Console.WriteLine("PASS SSDP: lost first request recovered, burst replies retained, duplicate USNs deduplicated");
            return 0;
        }
    }
}
