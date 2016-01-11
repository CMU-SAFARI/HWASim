//#define debug

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace ICSimulator
{
    public class Network
    {
        // dimensions
        public int X, Y;

        // every node has a router and a processor; not every processor is an application proc
        public Router[] routers;
        public Node[] nodes;
        public List<Link> links;
        public Workload workload;
        public Golden golden;
        public NodeMapping mapping;
        public CmpCache cache;

        // finish mode
        public enum FinishMode { app, insn, synth, cycle, barrier };
        FinishMode m_finish;
        ulong m_finishCount;

        public FinishMode finishMode { get { return m_finish; } }

        public double _cycle_netutil; // HACK -- controllers can use this.
        public ulong _cycle_insns;    // HACK -- controllers can use this.
        public ulong _cycle_L1_misses;    // HACK -- controllers can use this.
        public double _afc_buffer_util; // HACK -- controllers can use this.
        public double _afc_total_netutil; // HACK -- controllers can use this.
        public ulong _ejectPacketCount; // HACK -- controllers can use this.
        public double _fullBufferPercentage; // HACK -- controllers can use this.

        public Network(int dimX, int dimY)
        {
            X = dimX;
            Y = dimY;
        }

        public void setup()
        {
            routers = new Router[Config.N];
            nodes = new Node[Config.N];
            links = new List<Link>();
            cache = new CmpCache();

            ParseFinish(Config.finish);

            workload = new Workload(Config.traceFilenames);

            mapping = new NodeMapping_AllCPU_SharedCache();

            // create routers and nodes
            for (int n = 0; n < Config.N; n++)
            {
                Coord c = new Coord(n);
                nodes[n] = new Node(mapping, c);
                routers[n] = MakeRouter(c);
                nodes[n].setRouter(routers[n]);
                routers[n].setNode(nodes[n]);
            }

            // create the Golden manager
            golden = new Golden();

            if (Config.RouterEvaluation)
                return;

            // connect the network with Links
            for (int n = 0; n < Config.N; n++)
            {
                int x, y;
                Coord.getXYfromID(n, out x, out y);

                // inter-router links
                for (int dir = 0; dir < 4; dir++)
                {
                    int oppDir = (dir + 2) % 4; // direction from neighbor's perspective

                    // determine neighbor's coordinates
                    int x_, y_;
                    switch (dir)
                    {
                        case Simulator.DIR_UP: x_ = x; y_ = y + 1; break;
                        case Simulator.DIR_DOWN: x_ = x; y_ = y - 1; break;
                        case Simulator.DIR_RIGHT: x_ = x + 1; y_ = y; break;
                        case Simulator.DIR_LEFT: x_ = x - 1; y_ = y; break;
                        default: continue;
                    }

                    // If we are a torus, we manipulate x_ and y_
                    if(Config.torus)
                    {
                      if(x_ < 0)
                        x_ += X;
                      else if(x_ >= X)
                        x_ -= X;

                      if(y_ < 0)
                        y_ += Y;
                      else if(y_ >= Y)
                        y_ -= Y;
                    }
                    // mesh, not torus: detect edge
                    else if (x_ < 0 || x_ >= X || y_ < 0 || y_ >= Y)
                    {
                        if (Config.edge_loop)
                        {
                            Link edgeL = new Link(Config.router.linkLatency - 1, n, dir);
                            links.Add(edgeL);

                            routers[Coord.getIDfromXY(x, y)].linkOut[dir] = edgeL;
                            routers[Coord.getIDfromXY(x, y)].linkIn[dir] = edgeL;
                            routers[Coord.getIDfromXY(x, y)].neighbors++;
                            routers[Coord.getIDfromXY(x, y)].neigh[dir] =
                                routers[Coord.getIDfromXY(x, y)];
                        }

                        continue;
                    }

                    // ensure no duplication by handling a link at the lexicographically
                    // first router
                    if (x_ < x || (x_ == x && y_ < y)) continue;

                    // Link param is *extra* latency (over the standard 1 cycle)
                    Link dirA = new Link(Config.router.linkLatency - 1, n, dir);
                    Link dirB = new Link(Config.router.linkLatency - 1, n, dir);
                    links.Add(dirA);
                    links.Add(dirB);

                    // link 'em up
                    routers[Coord.getIDfromXY(x, y)].linkOut[dir] = dirA;
                    routers[Coord.getIDfromXY(x_, y_)].linkIn[oppDir] = dirA;

                    routers[Coord.getIDfromXY(x, y)].linkIn[dir] = dirB;
                    routers[Coord.getIDfromXY(x_, y_)].linkOut[oppDir] = dirB;

                    routers[Coord.getIDfromXY(x, y)].neighbors++;
                    routers[Coord.getIDfromXY(x_, y_)].neighbors++;

                    routers[Coord.getIDfromXY(x, y)].neigh[dir] = routers[Coord.getIDfromXY(x_, y_)];
                    routers[Coord.getIDfromXY(x_, y_)].neigh[oppDir] = routers[Coord.getIDfromXY(x, y)];

                    if (Config.router.algorithm == RouterAlgorithm.DR_SCARAB)
                    {
                        for (int wireNr = 0; wireNr < Config.nack_nr; wireNr++)
                        {
                            Link nackA = new Link(Config.nack_linkLatency - 1, n, dir);
                            Link nackB = new Link(Config.nack_linkLatency - 1, n, dir);
                            links.Add(nackA);
                            links.Add(nackB);
                            ((Router_SCARAB)routers[Coord.getIDfromXY(x, y)]).nackOut[dir * Config.nack_nr + wireNr] = nackA;
                            ((Router_SCARAB)routers[Coord.getIDfromXY(x_, y_)]).nackIn[oppDir * Config.nack_nr + wireNr] = nackA;

                            ((Router_SCARAB)routers[Coord.getIDfromXY(x, y)]).nackIn[dir * Config.nack_nr + wireNr] = nackB;
                            ((Router_SCARAB)routers[Coord.getIDfromXY(x_, y_)]).nackOut[oppDir * Config.nack_nr + wireNr] = nackB;
                        }
                    }
                }
            }

            if (Config.torus)
                for (int i = 0; i < Config.N; i++)
                    if (routers[i].neighbors < 4)
                        throw new Exception("torus construction not successful!");
        }

        public void ftflySetup()
        {
            if (Config.router.algorithm != RouterAlgorithm.DR_FLIT_SWITCHED_OLDEST_FIRST
                    && Config.router.algorithm != RouterAlgorithm.DR_AFC
                    && Config.router.algorithm != RouterAlgorithm.DR_FLIT_SWITCHED_CALF)
                throw new Exception(String.Format("Flattened butteryfly network does not support {0}", Config.router.algorithm));

            routers = new Router[Config.N];
            nodes = new Node[Config.N];
            links = new List<Link>();
            cache = new CmpCache();

            ParseFinish(Config.finish);

            workload = new Workload(Config.traceFilenames);

            mapping = new NodeMapping_AllCPU_SharedCache();

            // create routers and nodes
            for (int n = 0; n < Config.N; n++)
            {
                Coord c = new Coord(n);
                nodes[n] = new Node(mapping, c);
                routers[n] = MakeRouter(c);
                nodes[n].setRouter(routers[n]);
                routers[n].setNode(nodes[n]);
            }

            // create the Golden manager
            golden = new Golden();

            if (Config.RouterEvaluation)
                return;

            // connect the network with Links
            for (int n = 0; n < Config.N; n++)
            {
                int x, y;
                Coord.getXYfromID(n, out x, out y);
#if DEBUG
                Console.WriteLine("NETWORK SETUP: coord ({0},{1}) ID {2}",x,y,n);
#endif
                // inter-router links
                for (int dir = 0; dir < 8; dir++)
                {
                    //if same coordinates
                    if(dir==x)
                        continue;
                    if(dir>=4 && dir%4==y)
                        continue;

                    int oppDir = (dir<4)?(x):(4+y);// direction from neighbor's perspective

                    // determine neighbor's coordinates
                    int x_, y_;
                    switch (dir)
                    {
                        case Simulator.DIR_Y_0: x_ = x; y_ = 0;break;
                        case Simulator.DIR_Y_1: x_ = x; y_ = 1; break;
                        case Simulator.DIR_Y_2: x_ = x; y_ = 2; break;
                        case Simulator.DIR_Y_3: x_ = x; y_ = 3; break;
                        case Simulator.DIR_X_0: x_ = 0; y_ = y; break;
                        case Simulator.DIR_X_1: x_ = 1; y_ = y; break;
                        case Simulator.DIR_X_2: x_ = 2; y_ = y; break;
                        case Simulator.DIR_X_3: x_ = 3; y_ = y; break;
                        default: continue;
                    }
                    // mesh, not torus: detect edge
                    // This part is for torus setup
                    if (x_ < 0 || x_ >= X || y_ < 0 || y_ >= Y)
                    {
                        if (Config.edge_loop)
                        {
                            Link edgeL = new Link(Config.router.linkLatency - 1, n, dir);
                            links.Add(edgeL);

                            routers[Coord.getIDfromXY(x, y)].linkOut[dir] = edgeL;
                            routers[Coord.getIDfromXY(x, y)].linkIn[dir] = edgeL;
                            routers[Coord.getIDfromXY(x, y)].neighbors++;
                            throw new Exception("FTFLY shouldn't hit a torus network setup(edge_loop=true).");
                        }

                        continue;
                    }

                    // ensure no duplication by handling a link at the lexicographically
                    // first router
                    // for flattened butterfly, it's fine cuz it's going to overwrite it
                    if (x_ < x || (x_ == x && y_ < y)) continue;

                    //Console.WriteLine("dst ({0},{1})",x_,y_);

                    /* The setup is row wise going upward */
                    int link_lat=Config.router.linkLatency-1;

                    // Extra latency to links that have longer hops.
                    if(Math.Abs(x-x_)>1)
                        link_lat+=Math.Abs(x-x_);
                    if(Math.Abs(y-y_)>1)
                        link_lat+=Math.Abs(y-y_);

                    // an extra cycle on router b/c of higher radix routers
                    // for bless. However, this extra cycle is modeled in
                    // chipper by having a larger sorting network.
                    if (Config.router.algorithm != RouterAlgorithm.DR_FLIT_SWITCHED_CALF)
                        link_lat++;

                    Link dirA = new Link(link_lat, n, dir);
                    Link dirB = new Link(link_lat, n, dir);
                    links.Add(dirA);
                    links.Add(dirB);

                    // link 'em up
                    routers[Coord.getIDfromXY(x, y)].linkOut[dir] = dirA;
                    routers[Coord.getIDfromXY(x_, y_)].linkIn[oppDir] = dirA;

                    routers[Coord.getIDfromXY(x, y)].linkIn[dir] = dirB;
                    routers[Coord.getIDfromXY(x_, y_)].linkOut[oppDir] = dirB;

                    routers[Coord.getIDfromXY(x, y)].neighbors++;
                    routers[Coord.getIDfromXY(x_, y_)].neighbors++;

                    routers[Coord.getIDfromXY(x, y)].neigh[dir] = routers[Coord.getIDfromXY(x_, y_)];
                    routers[Coord.getIDfromXY(x_, y_)].neigh[oppDir] = routers[Coord.getIDfromXY(x, y)];
    
                    // DONE CARE for ftfly
                    if (Config.router.algorithm == RouterAlgorithm.DR_SCARAB)
                    {
                        for (int wireNr = 0; wireNr < Config.nack_nr; wireNr++)
                        {
                            Link nackA = new Link(Config.nack_linkLatency - 1, n, dir);
                            Link nackB = new Link(Config.nack_linkLatency - 1, n, dir);
                            links.Add(nackA);
                            links.Add(nackB);
                            ((Router_SCARAB)routers[Coord.getIDfromXY(x, y)]).nackOut[dir * Config.nack_nr + wireNr] = nackA;
                            ((Router_SCARAB)routers[Coord.getIDfromXY(x_, y_)]).nackIn[oppDir * Config.nack_nr + wireNr] = nackA;

                            ((Router_SCARAB)routers[Coord.getIDfromXY(x, y)]).nackIn[dir * Config.nack_nr + wireNr] = nackB;
                            ((Router_SCARAB)routers[Coord.getIDfromXY(x_, y_)]).nackOut[oppDir * Config.nack_nr + wireNr] = nackB;
                        }
                    }
                }
            }
            for (int n = 0; n < Config.N; n++)
            {
               int x, y;
               Coord.getXYfromID(n, out x, out y);
               //Console.WriteLine("router {0}-({1},{2}):{3}",n,x,y,routers[n].neighbors); 
            }
        }


        public void doStep()
        {
            doStats();

            // step the golden controller
            golden.doStep();

            // step the nodes
            for (int n = 0; n < Config.N; n++)
                nodes[n].doStep();

            // step the network sim: first, routers
            for (int n = 0; n < Config.N; n++)
                routers[n].doStep();

            // now, step each link
            foreach (Link l in links)
                l.doStep();
        }

        void doStats()
        {
            int used_links = 0;
            int[] usedLinksCount = new int[Config.N];
            int[] usedLinksCountbyReq = new int[Config.N];

            foreach (Link l in links)
            {
                if (l.Out != null)
                {
                    used_links++;
                    usedLinksCount[l.Out.packet.src.ID]++;
                    usedLinksCountbyReq[l.Out.packet.requesterID]++;
                    Simulator.stats.link_traversal_bysrc[l.Out.packet.src.ID].Add();
                    l.doStat();
                }
            }

            for (int i = 0; i < Config.N; i++)
            {
                Simulator.stats.netutil_bysrc[i].Add((double)usedLinksCount[i] / links.Count);
                Simulator.stats.netutil_byreqID[i].Add((double)usedLinksCountbyReq[i] / links.Count);
            }

            this._cycle_netutil = (double)used_links / links.Count;

            Simulator.stats.netutil.Add(this._cycle_netutil);
            Simulator.stats.mshrThrottle_netutil.Add(this._cycle_netutil);
            Simulator.stats.mshrThrottle_smallEpoch_netutil.Add(this._cycle_netutil);

            this._cycle_insns = 0; // CPUs increment this each cycle -- we want a per-cycle sum
            this._cycle_L1_misses = 0; // CPUs increment this each cycle -- we want a per-cycle sum
            
            if (Config.router.algorithm == RouterAlgorithm.DR_AFC)
            {
                int cap = 0;
                int count = 0;
                foreach (Router r in routers)
                {
                    Router_AFC rAFC = (Router_AFC)r;
                    // Don't count in buffers when in bless mode
                    if (rAFC.isBuffered)
                    {
                        cap += rAFC.totalBufCap();
                        count += rAFC.totalBufCount();
                    }
                }

                if (cap != 0)
                    this._afc_buffer_util = (double)count / cap;
                else 
                    this._afc_buffer_util = 0;

                this._afc_total_netutil = (double)(used_links + count) / (cap + links.Count);
                Simulator.stats.afc_buf_util.Add(this._afc_buffer_util);
                Simulator.stats.afc_total_util.Add(this._afc_total_netutil);
                Simulator.stats.mshrThrottle_afc_total_util.Add(this._afc_total_netutil);
#if debug
                if (this._afc_buffer_util != 0)
                {
                    Console.WriteLine("buffer util {0}", this._afc_buffer_util);
                    Console.WriteLine("total util {0}", this._afc_total_netutil);
                }
#endif
        
                // Record packet eject count over the last period
                _ejectPacketCount = 0;
                _fullBufferPercentage = 0.0;
                int fullCount = 0;
                foreach (Router r in routers)
                {
                    Router_AFC rAFC = (Router_AFC)r;
                    fullCount += rAFC.fullBuffer();
                    _ejectPacketCount += r.ejectPacketCount;
                    // reset since we only want to know the count during the last period
                    r.ejectPacketCount = 0;
                }
                int totalBufCount = (int)Config.N * Router.TOTAL_PORTS * Config.afc_vnets;
                _fullBufferPercentage = (double)fullCount / totalBufCount;
                //Console.WriteLine("percentage {0}", _fullBufferPercentage);
            }
        }

        public bool isFinished()
        {
            switch (m_finish)
            {
                case FinishMode.app:
                    int count = 0;
                    for (int i = 0; i < Config.N; i++)
                        if (nodes[i].Finished) count++;

                    return count == Config.N;

                case FinishMode.cycle:
                    return Simulator.CurrentRound >= m_finishCount;

                case FinishMode.barrier:
                    return Simulator.CurrentBarrier >= (ulong)Config.barrier;
            }

            throw new Exception("unknown finish mode");
        }

        public bool isLivelocked()
        {
            for (int i = 0; i < Config.N; i++)
                if (nodes[i].Livelocked) return true;
            return false;
        }

        void ParseFinish(string finish)
        {
            // finish is "app", "insn <n>", "synth <n>", or "barrier <n>"

            string[] parts = finish.Split(' ', '=');
            if (parts[0] == "app")
                m_finish = FinishMode.app;
            else if (parts[0] == "cycle")
                m_finish = FinishMode.cycle;
            else if (parts[0] == "barrier")
                m_finish = FinishMode.barrier;
            else
                throw new Exception("unknown finish mode");

            if (m_finish == FinishMode.app || m_finish == FinishMode.barrier)
                m_finishCount = 0;
            else
                m_finishCount = UInt64.Parse(parts[1]);
        }


        public void close()
        {
            for (int n = 0; n < Config.N; n++)
                routers[n].close();
        }

        public void visitFlits(Flit.Visitor fv)
        {
            foreach (Link l in links)
                l.visitFlits(fv);
            foreach (Router r in routers)
                r.visitFlits(fv);
            foreach (Node n in nodes)
                n.visitFlits(fv);
        }

        public delegate Flit FlitInjector();

        public int injectFlits(int count, FlitInjector fi)
        {
            int i = 0;
            for (; i < count; i++)
            {
                bool found = false;
                foreach (Router r in routers)
                    if (r.canInjectFlit(null))
                    {
                        r.InjectFlit(fi());
                        found = true;
                        break;
                    }

                if (!found)
                    return i;
            }

            return i;
        }

        public static Router MakeRouter(Coord c)
        {
            switch (Config.router.algorithm)
            {
                case RouterAlgorithm.DR_AFC:
                    return new Router_AFC(c);

                case RouterAlgorithm.DR_AFC_GPU:
                    return new Router_GPU(c);
                case RouterAlgorithm.DR_FLIT_SWITCHED_CTLR:
                    return new Router_Flit_Ctlr(c);

                case RouterAlgorithm.DR_FLIT_SWITCHED_CTLR_INJECTPRIO:
                    return new Router_Flit_Ctlr_InjectPrio(c);

                case RouterAlgorithm.DR_FLIT_SWITCHED_OLDEST_FIRST:
                    return new Router_Flit_OldestFirst(c);

                case RouterAlgorithm.DR_SCARAB:
                    return new Router_SCARAB(c);

                case RouterAlgorithm.DR_FLIT_SWITCHED_GP:
                    return new Router_Flit_GP(c);

                case RouterAlgorithm.DR_FLIT_SWITCHED_CALF:
                    return new Router_SortNet_GP(c);

                case RouterAlgorithm.DR_FLIT_SWITCHED_CALF_OF:
                    return new Router_SortNet_OldestFirst(c);

                case RouterAlgorithm.DR_FLIT_SWITCHED_RANDOM:
                    return new Router_Flit_Random(c);

                case RouterAlgorithm.ROUTER_FLIT_EXHAUSTIVE:
                    return new Router_Flit_Exhaustive(c);

                case RouterAlgorithm.OLDEST_FIRST_DO_ROUTER:
                    return new OldestFirstDORouter(c);

                case RouterAlgorithm.ROUND_ROBIN_DO_ROUTER:
                    return new RoundRobinDORouter(c);

                case RouterAlgorithm.STC_DO_ROUTER:
                    return new STC_DORouter(c);

                default:
                    throw new Exception("invalid routing algorithm " + Config.router.algorithm);
            }
        }
    }
}
