namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
    }
    public class GameObject
    {
        public Character Character = new Character();
        public T GetComponent<T>() where T : class => Character as T;
    }
    public static class Time { public static float realtimeSinceStartup = 1; }
    public class Transform { public Vector3 position; }
}
public struct ZDOID : System.IEquatable<ZDOID>
{
    public long UserID;
    public uint ID;
    public ZDOID(long uid, uint id) { UserID=uid; ID=id; }
    public bool IsNone() => UserID==0 && ID==0;
    public bool Equals(ZDOID other) => UserID==other.UserID && ID==other.ID;
    public override bool Equals(object other) => other is ZDOID id && Equals(id);
    public override int GetHashCode() => UserID.GetHashCode() ^ (int)ID;
    public static bool operator ==(ZDOID a, ZDOID b) => a.Equals(b);
    public static bool operator !=(ZDOID a, ZDOID b) => !a.Equals(b);
}
public class ZDO
{
    public long Owner;
    public int Prefab;
    public UnityEngine.Vector3 Position;
    public long GetOwner() => Owner;
    public int GetPrefab() => Prefab;
    public UnityEngine.Vector3 GetPosition() => Position;
}
public class ZDOMan
{
    public static ZDOMan instance = new ZDOMan();
    public System.Collections.Generic.Dictionary<ZDOID,ZDO> Objects = new System.Collections.Generic.Dictionary<ZDOID,ZDO>();
    public ZDO GetZDO(ZDOID id) => Objects.TryGetValue(id, out var zdo) ? zdo : null;
}
public class ZNetScene
{
    public static ZNetScene instance = new ZNetScene();
    public UnityEngine.GameObject Monster = new UnityEngine.GameObject();
    public UnityEngine.GameObject GetPrefab(int hash) => hash == "Goblin".GetStableHashCode() ? Monster : null;
}
public class Character { }
public class Player : Character
{
    public static Player m_localPlayer;
    public ZDOID Id;
    public bool Dead;
    public UnityEngine.Transform transform = new UnityEngine.Transform();
    public ZDOID GetZDOID() => Id;
    public bool IsDead() => Dead;
}
public class ZNet
{
    public static ZNet instance;
    public bool Server = true;
    public ZNetPeer ServerPeer;
    public System.Collections.Generic.List<ZNetPeer> Peers = new System.Collections.Generic.List<ZNetPeer>();
    public bool IsServer() => Server;
    public static long GetUID() => 1;
    public ZNetPeer GetServerPeer() => ServerPeer;
    public System.Collections.Generic.List<ZNetPeer> GetConnectedPeers() => Peers;
}
public class ZNetPeer
{
    public ZRpc m_rpc = new ZRpc();
    public long m_uid;
    public ZDOID m_characterID;
    public bool Ready = true;
    public Clan.ClanPlayerRef Identity;
    public bool IsReady() => Ready;
}
public class ZRpc
{
    public System.Collections.Generic.Dictionary<string,System.Action<ZRpc,ZPackage>> Handlers = new System.Collections.Generic.Dictionary<string,System.Action<ZRpc,ZPackage>>();
    public System.Collections.Generic.List<ZPackage> Sent = new System.Collections.Generic.List<ZPackage>();
    public void Register<T>(string name, System.Action<ZRpc,T> handler) => Handlers[name] = (rpc, p) => handler(rpc, (T)(object)p);
    public void Invoke(string name, params object[] args) => Sent.Add((ZPackage)args[0]);
    public void Receive(string name, ZPackage package) { package.SetPos(0); Handlers[name](this, package); }
}
public class ZPackage
{
    private readonly System.IO.MemoryStream _stream = new System.IO.MemoryStream();
    private System.IO.BinaryWriter Writer => new System.IO.BinaryWriter(_stream, System.Text.Encoding.UTF8, true);
    private System.IO.BinaryReader Reader => new System.IO.BinaryReader(_stream, System.Text.Encoding.UTF8, true);
    public int Size() => (int)_stream.Length;
    public int GetPos() => (int)_stream.Position;
    public void SetPos(int pos) => _stream.Position = pos;
    public void Write(byte v) => Writer.Write(v);
    public void Write(long v) => Writer.Write(v);
    public void Write(int v) => Writer.Write(v);
    public void Write(string v) => Writer.Write(v);
    public void Write(ZDOID v) { Write(v.UserID); Writer.Write(v.ID); }
    public void Write(UnityEngine.Vector3 v) { Writer.Write(v.x); Writer.Write(v.y); Writer.Write(v.z); }
    public byte ReadByte() => Reader.ReadByte();
    public long ReadLong() => Reader.ReadInt64();
    public int ReadInt() => Reader.ReadInt32();
    public string ReadString() => Reader.ReadString();
    public ZDOID ReadZDOID() => new ZDOID(ReadLong(), Reader.ReadUInt32());
    public UnityEngine.Vector3 ReadVector3() => new UnityEngine.Vector3(Reader.ReadSingle(),Reader.ReadSingle(),Reader.ReadSingle());
}
public static class StringHash
{
    public static int GetStableHashCode(this string value) { int hash=0; foreach(char c in value) hash=hash*31+c; return hash; }
}
namespace Clan
{
    public struct ClanPlayerRef : System.IEquatable<ClanPlayerRef>
    {
        public string PlatformId;
        public long CharacterPlayerId;
        public bool IsValid => !string.IsNullOrEmpty(PlatformId) && CharacterPlayerId>0;
        public bool Equals(ClanPlayerRef b) => PlatformId==b.PlatformId && CharacterPlayerId==b.CharacterPlayerId;
        public override bool Equals(object b) => b is ClanPlayerRef p && Equals(p);
        public override int GetHashCode() => CharacterPlayerId.GetHashCode();
        public static bool operator ==(ClanPlayerRef a, ClanPlayerRef b) => a.Equals(b);
        public static bool operator !=(ClanPlayerRef a, ClanPlayerRef b) => !a.Equals(b);
    }
    public enum ClanMembershipResolution { Unavailable, Resolved }
    public class Config<T> { public T Value; public Config(T v) { Value=v; } }
    public static class ClanPlugin
    {
        public static Config<bool> ShareEpicMmoExperience=new Config<bool>(true), ShareQuestForgeKills=new Config<bool>(true);
        public static Config<float> QuestForgeShareRange=new Config<float>(70);
        public static Log ClanLogger=new Log();
    }
    public class Log { public void LogWarning(string message) { } }
    public static class ToggleExtensions { public static bool IsOn(this bool v) => v; }
    public static class ClanIdentity
    {
        public static ClanPlayerRef Local;
        public static ClanPlayerRef FromPeer(ZNetPeer peer) => peer==null ? Local : peer.Identity;
    }
    public static class ClanRpc
    {
        public static System.Collections.Generic.Dictionary<ZNetPeer,ClanPlayerRef> Pins=new System.Collections.Generic.Dictionary<ZNetPeer,ClanPlayerRef>();
        public static bool TryGetPinnedPeerIdentity(ZNetPeer peer, out ClanPlayerRef identity)
        { identity=default; return peer!=null && Pins.TryGetValue(peer,out identity); }
    }
    public static class ClanApi
    {
        public static System.Collections.Generic.Dictionary<long,string[]> Memberships=new System.Collections.Generic.Dictionary<long,string[]>();
        public static ClanMembershipResolution ResolveMemberships(string platform,long player,out string primary,out string pn,out string guest,out string gn)
        {
            primary=guest=pn=gn="";
            if(!Memberships.TryGetValue(player,out var clans)) return ClanMembershipResolution.Unavailable;
            primary=clans[0]; guest=clans[1]; return ClanMembershipResolution.Resolved;
        }
    }
    public static class EpicMmoCompat
    {
        public static bool IsReady=true;
        public static System.Collections.Generic.List<int> Awards=new System.Collections.Generic.List<int>();
        public static void ReceiveExperience(long killer,int xp,UnityEngine.Vector3 pos,int level) => Awards.Add(xp);
    }
    public static class QuestForgeCompat
    {
        public static bool IsReady=true;
        public static System.Collections.Generic.List<string> Credits=new System.Collections.Generic.List<string>();
        public static void ReceiveKill(string prefab) => Credits.Add(prefab);
    }
    public static class GroupSharingTests
    {
        private const string Request="Clan_GroupCreditRequest_v1", Response="Clan_GroupCreditResponse_v1";
        private static int _checks;
        private static ZDOID _victim=new ZDOID(3,99);
        private static void Check(bool condition,string label) { if(!condition) throw new System.Exception(label); _checks++; }
        private static ClanPlayerRef Person(long id) => new ClanPlayerRef { PlatformId="Steam_"+id, CharacterPlayerId=id*10 };
        private static ZNetPeer Peer(long uid)
        {
            var p=new ZNetPeer { m_uid=uid, m_characterID=new ZDOID(uid,1), Identity=Person(uid) };
            ZNet.instance.Peers.Add(p);
            ZDOMan.instance.Objects[p.m_characterID]=new ZDO();
            ClanGroupSharing.RegisterPeer(ZNet.instance,p);
            return p;
        }
        private static ZNetPeer Setup()
        {
            ClanGroupSharing.ResetSession(); ClanRpc.Pins.Clear(); ClanApi.Memberships.Clear();
            EpicMmoCompat.Awards.Clear(); QuestForgeCompat.Credits.Clear();
            EpicMmoCompat.IsReady=QuestForgeCompat.IsReady=true;
            ClanPlugin.ShareEpicMmoExperience.Value=ClanPlugin.ShareQuestForgeKills.Value=true;
            UnityEngine.Time.realtimeSinceStartup=1;
            ZNet.instance=new ZNet(); ZDOMan.instance=new ZDOMan(); ZNetScene.instance=new ZNetScene();
            Player.m_localPlayer=new Player { Id=new ZDOID(1,1) }; ClanIdentity.Local=Person(1);
            ZDOMan.instance.Objects[Player.m_localPlayer.Id]=new ZDO();
            var killer=Peer(2); Peer(3); Peer(4); Peer(5);
            // Same primary, but player 4 is a guest elsewhere and must not double-dip.
            ClanApi.Memberships[10]=new[]{"A",""}; ClanApi.Memberships[20]=new[]{"A",""};
            ClanApi.Memberships[30]=new[]{"B","A"}; ClanApi.Memberships[40]=new[]{"A","B"};
            ClanApi.Memberships[50]=new[]{"C",""};
            ZDOMan.instance.Objects[_victim]=new ZDO { Owner=3, Prefab="Goblin".GetStableHashCode() };
            return killer;
        }
        private static ZPackage Epic(long seq,int xp=70)
        {
            var p=new ZPackage();p.Write((byte)1);p.Write(seq);p.Write(xp);p.Write(new UnityEngine.Vector3());p.Write(20);return p;
        }
        private static ZPackage Kill(long seq,ZDOID? killer=null,string prefab="Goblin",UnityEngine.Vector3? pos=null)
        {
            var p=new ZPackage();p.Write((byte)2);p.Write(seq);p.Write(_victim);p.Write(killer??new ZDOID(2,1));
            p.Write(prefab);p.Write(pos??new UnityEngine.Vector3());return p;
        }
        private static ZPackage ResponsePacket(byte kind,long seq,ZDOID target)
        {
            var p=new ZPackage();p.Write(kind);p.Write(seq);p.Write(target);
            if(kind==1) {p.Write(2L);p.Write(70);p.Write(new UnityEngine.Vector3());p.Write(20);}
            else {p.Write(_victim);p.Write("Goblin");p.Write(new UnityEngine.Vector3());}
            return p;
        }
        public static string Run()
        {
            var killer=Setup(); var owner=ZNet.instance.Peers[1];
            killer.m_rpc.Receive(Request,Epic(1));
            Check(EpicMmoCompat.Awards.Count==1 && EpicMmoCompat.Awards[0]==70,"host receives unchanged XP");
            Check(killer.m_rpc.Sent.Count==0 && owner.m_rpc.Sent.Count==1,"exclude killer; guest receives");
            Check(ZNet.instance.Peers[2].m_rpc.Sent.Count==0 && ZNet.instance.Peers[3].m_rpc.Sent.Count==0,"guest elsewhere and outsider excluded");
            killer.m_rpc.Receive(Request,Epic(1)); Check(EpicMmoCompat.Awards.Count==1,"report replay rejected");
            ClanPlugin.ShareEpicMmoExperience.Value=false; killer.m_rpc.Receive(Request,Epic(2));
            Check(EpicMmoCompat.Awards.Count==1,"server live toggle");
            killer=Setup(); ClanRpc.Pins[killer]=Person(6);killer.m_rpc.Receive(Request,Epic(1));
            Check(EpicMmoCompat.Awards.Count==0,"pinned character mismatch");
            killer=Setup();killer.Ready=false;killer.m_rpc.Receive(Request,Epic(1));
            Check(EpicMmoCompat.Awards.Count==0,"unready peer");
            killer=Setup();ZNet.instance.Peers.Remove(killer);killer.m_rpc.Receive(Request,Epic(1));
            Check(EpicMmoCompat.Awards.Count==0,"disconnected peer cannot report");
            killer=Setup();ClanApi.Memberships.Remove(20);killer.m_rpc.Receive(Request,Epic(1));
            Check(EpicMmoCompat.Awards.Count==0,"membership unavailable fails closed");
            killer=Setup();var trailing=Epic(1);trailing.Write(42);killer.m_rpc.Receive(Request,trailing);
            Check(EpicMmoCompat.Awards.Count==0,"trailing payload rejected");
            killer.m_rpc.Receive(Request,new ZPackage()); Check(EpicMmoCompat.Awards.Count==0,"truncated packet contained");
            var oversized=new ZPackage();oversized.Write(new string('x',600));killer.m_rpc.Receive(Request,oversized);
            Check(EpicMmoCompat.Awards.Count==0,"oversized packet rejected");
            killer=Setup();Player.m_localPlayer.Dead=true;killer.m_rpc.Receive(Request,Epic(1));
            Check(EpicMmoCompat.Awards.Count==1,"Epic receiver death policy is not changed by the relay");
            killer=Setup();owner=ZNet.instance.Peers[1];owner.m_rpc.Receive(Request,Kill(1));
            Check(QuestForgeCompat.Credits.Count==1 && QuestForgeCompat.Credits[0]=="Goblin","different owner reports; host receives quest credit");
            Check(killer.m_rpc.Sent.Count==0 && owner.m_rpc.Sent.Count==1,"quest killer excluded, eligible owner receives");
            owner.m_rpc.Receive(Request,Kill(2));Check(QuestForgeCompat.Credits.Count==1,"same death with new sequence rejected");
            ClanGroupSharing.ForgetPeer(owner.m_rpc);owner.m_rpc.Receive(Request,Kill(3));
            Check(QuestForgeCompat.Credits.Count==1,"peer-state cleanup does not erase death dedupe");
            killer=Setup();killer.m_rpc.Receive(Request,Kill(1));Check(QuestForgeCompat.Credits.Count==0,"non-owner report rejected");
            killer=Setup();owner=ZNet.instance.Peers[1];ZDOMan.instance.Objects.Remove(_victim);owner.m_rpc.Receive(Request,Kill(1));
            Check(QuestForgeCompat.Credits.Count==0,"unknown/destroyed victim rejected");
            killer=Setup();owner=ZNet.instance.Peers[1];owner.m_rpc.Receive(Request,Kill(1,prefab:"Troll"));
            Check(QuestForgeCompat.Credits.Count==0,"prefab spoof rejected");
            owner.m_rpc.Receive(Request,Kill(2,pos:new UnityEngine.Vector3(100,0,0)));
            Check(QuestForgeCompat.Credits.Count==0,"death position spoof rejected");
            owner.m_rpc.Receive(Request,Kill(3,killer:new ZDOID(99,1)));
            Check(QuestForgeCompat.Credits.Count==0,"unknown killer rejected");
            killer=Setup();owner=ZNet.instance.Peers[1];ZNetScene.instance.Monster.Character=new Player();owner.m_rpc.Receive(Request,Kill(1));
            Check(QuestForgeCompat.Credits.Count==0,"player victim rejected");
            killer=Setup();owner=ZNet.instance.Peers[1];Player.m_localPlayer.Dead=true;owner.m_rpc.Receive(Request,Kill(1));
            Check(QuestForgeCompat.Credits.Count==0,"dead host gets no quest credit");
            killer=Setup();owner=ZNet.instance.Peers[1];ZDOMan.instance.Objects[Player.m_localPlayer.Id].Position=new UnityEngine.Vector3(71,0,0);
            owner.m_rpc.Receive(Request,Kill(1));Check(QuestForgeCompat.Credits.Count==0,"server quest range");
            killer=Setup();owner=ZNet.instance.Peers[1];Player.m_localPlayer.transform.position=new UnityEngine.Vector3(71,0,0);
            owner.m_rpc.Receive(Request,Kill(1));Check(QuestForgeCompat.Credits.Count==0,"receiver quest range");
            Setup();ZDOMan.instance.Objects[_victim].Owner=1;
            ClanGroupSharing.ShareQuestKill(_victim,new ZDOID(2,1),"Goblin",new UnityEngine.Vector3());
            Check(QuestForgeCompat.Credits.Count==1,"host monster owner sends through same validation");
            Setup();var server=new ZNetPeer();ZNet.instance.Server=false;ZNet.instance.ServerPeer=server;
            ClanGroupSharing.RegisterPeer(ZNet.instance,server);
            var rogue=new ZNetPeer();ClanGroupSharing.RegisterPeer(ZNet.instance,rogue);
            rogue.m_rpc.Receive(Response,ResponsePacket(1,1,Player.m_localPlayer.Id));
            Check(EpicMmoCompat.Awards.Count==0,"non-server delivery rejected");
            server.m_rpc.Receive(Response,ResponsePacket(1,1,new ZDOID(1,2)));
            Check(EpicMmoCompat.Awards.Count==0,"old character delivery rejected");
            server.m_rpc.Receive(Response,ResponsePacket(1,2,Player.m_localPlayer.Id));
            server.m_rpc.Receive(Response,ResponsePacket(1,2,Player.m_localPlayer.Id));
            Check(EpicMmoCompat.Awards.Count==1,"server delivery exactly once per sequence");
            server.m_rpc.Receive(Response,ResponsePacket(2,3,Player.m_localPlayer.Id));
            server.m_rpc.Receive(Response,ResponsePacket(2,4,Player.m_localPlayer.Id));
            Check(QuestForgeCompat.Credits.Count==1,"consumer death dedupe");
            ClanGroupSharing.ResetSession();server.m_rpc.Receive(Response,ResponsePacket(2,1,Player.m_localPlayer.Id));
            Check(QuestForgeCompat.Credits.Count==2,"new session clears old death and sequence");
            killer=Setup();for(int i=1;i<=300;i++)killer.m_rpc.Receive(Request,Epic(i));
            Check(EpicMmoCompat.Awards.Count==256,"bounded report rate");
            UnityEngine.Time.realtimeSinceStartup=7;killer.m_rpc.Receive(Request,Epic(301));
            Check(EpicMmoCompat.Awards.Count==257,"rate window recovers");
            Setup();EpicMmoCompat.IsReady=false;ClanGroupSharing.ShareEpicExperience(70,new UnityEngine.Vector3(),20);
            Check(EpicMmoCompat.Awards.Count==0,"missing optional consumer does not execute");
            var cacheType=typeof(ClanGroupSharing).GetNestedType("DeathCredits",System.Reflection.BindingFlags.NonPublic);
            var cache=System.Activator.CreateInstance(cacheType,true);
            var accept=cacheType.GetMethod("Accept",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            for(uint i=1;i<=4096;i++) if(!(bool)accept.Invoke(cache,new object[]{new ZDOID(100,i),1f}))throw new System.Exception("early capacity rejection");
            Check(!(bool)accept.Invoke(cache,new object[]{new ZDOID(100,4097),2f}),"fresh death cache cannot exceed capacity");
            Check(!(bool)accept.Invoke(cache,new object[]{new ZDOID(100,1),2f}),"capacity pressure cannot evict replay protection");
            Check((bool)accept.Invoke(cache,new object[]{new ZDOID(100,4097),122f}),"expired death IDs release bounded capacity");
            return "PASS: "+_checks+" production group-sharing transport/eligibility checks (controlled substitutes; no Unity run).";
        }
    }
}
