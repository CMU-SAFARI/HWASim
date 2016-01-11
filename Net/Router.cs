//#define DEBUG

using System;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using System.IO;

namespace ICSimulator
{
    public abstract class Router
    {
        public Coord coord;
        public int ID { get { return coord.ID; } }

        public static bool _bFtfly = Config.bFtfly;
        public static int TOTAL_DIR=(_bFtfly==true)?8:4;
        public static int TOTAL_PORTS=(_bFtfly==true)?9:5;//fbfly: 8 links (2 self-loop that is null) + 1 local
        public static int LOCAL_INDEX=(_bFtfly==true)?TOTAL_PORTS-1:4;//this is used in buffered router
        public static int INPUT_NUM=(_bFtfly==true)?6:4;

        public Link[] linkOut = new Link[TOTAL_DIR];
        public Link[] linkIn = new Link[TOTAL_DIR];
        public Router[] neigh = new Router[TOTAL_DIR];

        public int neighbors;

        protected string routerName;
        protected Node m_n;

        // Keep track of the current router's average queue length over the
        // last INJ_RATE_WIN cycles
        public const int AVG_QLEN_WIN=1000;
        public float avg_qlen;
        public int[] qlen_win;
        public int qlen_ptr;
        public int qlen_count;
        public ulong m_lastInj;
        public ulong last_starve, starve_interval;

        public ulong m_inject = 0;
        public ulong Inject { get { return m_inject; } }

        public ulong ejectPacketCount = 0;

        // --------------------------------------------------------------------

        public struct PreferredDirection
        {
            public int xDir;
            public int yDir;
        }

        public Router(Coord myCoord)
        {
            coord = myCoord;

            m_n = Simulator.network.nodes[ID];
            routerName = "Router";

            neighbors = 0;

            m_lastInj = 0;
            last_starve = 0;
            starve_interval = 0;

            qlen_win = new int[AVG_QLEN_WIN];
        }

        public void setNode(Node n)
        {
            m_n = n;
        }


        /********************************************************
         * PUBLIC API
         ********************************************************/

        // called from Network
        public void doStep()
        {
            statsInput();
            _doStep();
            statsOutput();
        }

        protected abstract void _doStep(); // called from Network

        public abstract bool canInjectFlit(Flit f); // called from Processor
        public abstract void InjectFlit(Flit f); // called from Processor

        public virtual int rank(Flit f1, Flit f2) { return 0; }

        // finally, subclasses should call myProcessor.ejectFlit() whenever a flit
        // arrives (or is part of a reassembled packet ready to be delivered, or whatever)

        // also, subclasses should call statsInput() before removing
        // inputs, statsOutput() after placing outputs, and
        // statsInjectFlit(f) if a flit is injected,
        // and statsEjectFlit(f) if a flit is ejected.

        // for flush/retry mechanisms: clear all router state.
        public virtual void flush() { }

        /********************************************************
         * ROUTING HELPERS
         ********************************************************/

        protected PreferredDirection determineDirection(Flit f)
        {
            return determineDirection(f, new Coord(0, 0));
        }

        protected PreferredDirection determineDirection(Flit f, Coord current)
        {
            PreferredDirection pd;
            pd.xDir = Simulator.DIR_NONE;
            pd.yDir = Simulator.DIR_NONE;

            if (f.state == Flit.State.Placeholder) return pd;

            //if (f.packet.ID == 238)
            //    Console.WriteLine("packet 238 at ID ({0},{1}), wants ({2},{3})", current.x, current.y, f.packet.dest.x, f.packet.dest.y);
            
            return determineDirection(f.dest);
        }

        protected PreferredDirection determineDirection(Coord c)
        {
            PreferredDirection pd;
            pd.xDir = Simulator.DIR_NONE;
            pd.yDir = Simulator.DIR_NONE;

            if (Config.bFtfly == true)
            {
                if(c.x!=coord.x) 
                    pd.xDir = c.x;
                if(c.y!=coord.y)
                    pd.yDir = 4+c.y;// y direction index starts at 4
            }
            else if(Config.torus) {
              int x_sdistance = Math.Abs(c.x - coord.x);
              int x_wdistance = Config.network_nrX - Math.Abs(c.x - coord.x);
              int y_sdistance = Math.Abs(c.y - coord.y);
              int y_wdistance = Config.network_nrY - Math.Abs(c.y - coord.y);
              bool x_dright, y_ddown;

              x_dright = coord.x < c.x;
              y_ddown = c.y < coord.y;

              if(c.x == coord.x)
                pd.xDir = Simulator.DIR_NONE;
              else if(x_sdistance < x_wdistance)
                pd.xDir = (x_dright) ? Simulator.DIR_RIGHT : Simulator.DIR_LEFT;
              else
                pd.xDir = (x_dright) ? Simulator.DIR_LEFT : Simulator.DIR_RIGHT;

              if(c.y == coord.y)
                pd.yDir = Simulator.DIR_NONE;
              else if(y_sdistance < y_wdistance)
                pd.yDir = (y_ddown) ? Simulator.DIR_DOWN : Simulator.DIR_UP;
              else
                pd.yDir = (y_ddown) ? Simulator.DIR_UP : Simulator.DIR_DOWN;

            } else {
              if (c.x > coord.x)
                  pd.xDir = Simulator.DIR_RIGHT;
              else if (c.x < coord.x)
                  pd.xDir = Simulator.DIR_LEFT;
              else
                  pd.xDir = Simulator.DIR_NONE;

              if (c.y > coord.y)
                  pd.yDir = Simulator.DIR_UP;
              else if (c.y < coord.y)
                  pd.yDir = Simulator.DIR_DOWN;
              else
                  pd.yDir = Simulator.DIR_NONE;
            }

            if (Config.dor_only && pd.xDir != Simulator.DIR_NONE)
                pd.yDir = Simulator.DIR_NONE;

            return pd;
        }

        // returns true if the direction is good for this packet. 
        protected bool isDirectionProductive(Coord dest, int direction)
        {
            bool answer = false;
            if (Config.bFtfly == true)
            {
                if(direction<4)
                    answer = (dest.x == coord.x);
                else
                    answer = (dest.y == coord.y);
            }
            else
            {
                switch (direction)
                {
                    case Simulator.DIR_UP: answer = (dest.y > coord.y); break;
                    case Simulator.DIR_RIGHT: answer = (dest.x > coord.x); break;
                    case Simulator.DIR_LEFT: answer = (dest.x < coord.x); break;
                    case Simulator.DIR_DOWN: answer = (dest.y < coord.y); break;
                    default: throw new Exception("This function shouldn't be called in this case!");
                }
            }
            return answer;
        }

        protected int dimension_order_route(Flit f)
        {
            if (Config.bFtfly == true)
            {
#if DEBUG2
                Console.WriteLine("in dimension_order_route dest ({0},{1}) from ({2},{3})",
                        f.packet.dest.x,f.packet.dest.y,coord.x, coord.y);
#endif
                if(f.packet.dest.x!=coord.x) 
                    return f.packet.dest.x;
                if(f.packet.dest.y!=coord.y)
                    return 4+f.packet.dest.y;
                return f.packet.dest.x;
            }
            else
            {
                if (f.packet.dest.x < coord.x)
                    return Simulator.DIR_LEFT;
                else if (f.packet.dest.x > coord.x)
                    return Simulator.DIR_RIGHT;
                else if (f.packet.dest.y < coord.y)
                    return Simulator.DIR_DOWN;
                else if (f.packet.dest.y > coord.y)
                    return Simulator.DIR_UP;
                else //if the destination's coordinates are equal to the router's coordinates
                    return Simulator.DIR_UP;
            }
        }

        /********************************************************
         * STATISTICS
         ********************************************************/
        protected int incomingFlits;
        private void statsInput()
        {
            int goldenCount = 0;
            incomingFlits = 0;
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (linkIn[i] != null && linkIn[i].Out != null)
                {
                    linkIn[i].Out.Deflected = false;

                    if (Simulator.network.golden.isGolden(linkIn[i].Out))
                        goldenCount++;
                    incomingFlits++;
                }
            }
            Simulator.stats.golden_pernode.Add(goldenCount);
            Simulator.stats.golden_bycount[goldenCount].Add();

            Simulator.stats.traversals_pernode[incomingFlits].Add();
            //Simulator.stats.traversals_pernode_bysrc[ID,incomingFlits].Add();
        }

        private void statsOutput()
        {
            int deflected = 0;
            int unproductive = 0;
            int traversals = 0;
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (linkOut[i] != null && linkOut[i].In != null)
                {
                    if (linkOut[i].In.Deflected)
                    {
                        // deflected! (may still be productive, depending on deflection definition/DOR used)
                        deflected++;
                        linkOut[i].In.nrOfDeflections++;
                        Simulator.stats.deflect_flit_byloc[ID].Add();

                        if (linkOut[i].In.packet != null)
                        {
                            Simulator.stats.deflect_flit_bysrc[linkOut[i].In.packet.src.ID].Add();
                            Simulator.stats.deflect_flit_byreq[linkOut[i].In.packet.requesterID].Add();
                        }
                    }

                    if (!isDirectionProductive(linkOut[i].In.dest, i))
                    {
                        //unproductive!
                        unproductive++;
                        Simulator.stats.unprod_flit_byloc[ID].Add();

                        if (linkOut[i].In.packet != null)
                            Simulator.stats.unprod_flit_bysrc[linkOut[i].In.packet.src.ID].Add();
                    }
                    traversals++;
                    //linkOut[i].In.deflectTest();
                }
            }

            Simulator.stats.deflect_flit.Add(deflected);
            Simulator.stats.deflect_flit_byinc[incomingFlits].Add(deflected);
            Simulator.stats.unprod_flit.Add(unproductive);
            Simulator.stats.unprod_flit_byinc[incomingFlits].Add(unproductive);
            Simulator.stats.flit_traversals.Add(traversals);

            int qlen = m_n.RequestQueueLen;

            qlen_count -= qlen_win[qlen_ptr];
            qlen_count += qlen;

            // Compute the average queue length
            qlen_win[qlen_ptr] = qlen;
            if(++qlen_ptr >= AVG_QLEN_WIN) qlen_ptr=0;

            avg_qlen = (float)qlen_count / (float)AVG_QLEN_WIN;
        }

        protected void statsInjectFlit(Flit f)
        {
            Simulator.stats.inject_flit.Add();
            if (f.isHeadFlit) Simulator.stats.inject_flit_head.Add();
            if (f.packet != null)
            {
                Simulator.stats.inject_flit_bysrc[f.packet.src.ID].Add();
                // Pure request
                if (f.packet.src.ID == f.packet.requesterID)
                {
                    Simulator.stats.mshrThrottle_inject_flit_bysrc[f.packet.src.ID].Add();
                }
            }

            if (f.packet != null && f.packet.injectionTime == ulong.MaxValue)
                f.packet.injectionTime = Simulator.CurrentRound;
            f.injectionTime = Simulator.CurrentRound;

            ulong hoq = Simulator.CurrentRound - m_lastInj;
            m_lastInj = Simulator.CurrentRound;

            Simulator.stats.hoq_latency.Add(hoq);
            Simulator.stats.hoq_latency_bysrc[coord.ID].Add(hoq);

            m_inject++;
        }

        protected void statsEjectFlit(Flit f)
        {
            // Keep track of how many packets get ejected
            ejectPacketCount++; // actually eject flit count

            // per-flit latency stats
            ulong net_latency = Simulator.CurrentRound - f.injectionTime;
            ulong total_latency = Simulator.CurrentRound - f.packet.creationTime;
            ulong inj_latency = total_latency - net_latency;

#if DEBUG
            Console.WriteLine("Cycle: {2} PID: {0} FID: {1} | EJECT Coord ({4},{5}) createTime {3}",
                    f.packet.ID, f.flitNr,Simulator.CurrentRound,f.packet.injectionTime,coord.x,coord.y);
#endif

            Simulator.stats.flit_inj_latency.Add(inj_latency);
            Simulator.stats.flit_net_latency.Add(net_latency);

            // A rough estimate of number of cycles it would take for a flit to go 
            Simulator.stats.flit_net_latency_alone_byreqID[f.packet.requesterID].Add(
                    (int)Simulator.distance(f.packet.src, f.packet.dest) * Config.router.linkLatency);
            // Record the net latency for each requester to take care of data reply from random sources
            Simulator.stats.flit_net_latency_byreqID[f.packet.requesterID].Add(net_latency);

            Simulator.stats.mshrThrottle_flit_net_latency.Add(net_latency);
            Simulator.stats.flit_total_latency.Add(inj_latency);

            Simulator.stats.eject_flit.Add();
            Simulator.stats.eject_flit_bydest[f.packet.dest.ID].Add();
            int id = f.packet.dest.ID;
            // Collect stats on my own data reply only
            if (id == f.packet.requesterID)
                Simulator.stats.eject_flit_myReply[id].Add();

            Simulator.stats.eject_flit_byreqID[f.packet.requesterID].Add();

            int minpath = Math.Abs(f.packet.dest.x - f.packet.src.x) + Math.Abs(f.packet.dest.y - f.packet.src.y);
            Simulator.stats.minpath.Add(minpath);
            Simulator.stats.minpath_bysrc[f.packet.src.ID].Add(minpath);

            //f.dumpDeflections();
            Simulator.stats.deflect_perdist[f.distance].Add(f.nrOfDeflections);
            if(f.nrOfDeflections!=0)
                Simulator.stats.deflect_perflit_byreq[f.packet.requesterID].Add(f.nrOfDeflections);
        }

        protected void statsEjectPacket(Packet p)
        {
            // Keep track of how many packets get ejected
            //ejectPacketCount++;

            ulong net_latency = Simulator.CurrentRound - p.injectionTime;
            ulong total_latency = Simulator.CurrentRound - p.creationTime;

            if (p.requesterID != ID)
            {
                // a request from some other node. Add this latency to their latency pair.
                Simulator.stats.totalPacketLatency[p.requesterID, ID].Add(total_latency);
            }
            else
            {
                // a reply back from some other node. Add this latency to my latency pair.
                Simulator.stats.totalPacketLatency[ID, p.src.ID].Add(total_latency);
            }

            Simulator.stats.net_latency.Add(net_latency);
            Simulator.stats.total_latency.Add(total_latency);
            Simulator.stats.net_latency_bysrc[p.src.ID].Add(net_latency);
            Simulator.stats.net_latency_bydest[p.dest.ID].Add(net_latency);
            //Simulator.stats.net_latency_srcdest[p.src.ID, p.dest.ID].Add(net_latency);
            Simulator.stats.total_latency_bysrc[p.src.ID].Add(total_latency);
            Simulator.stats.total_latency_bydest[p.dest.ID].Add(total_latency);
            //Simulator.stats.total_latency_srcdest[p.src.ID, p.dest.ID].Add(total_latency);
        }

        public override string ToString()
        {
            return routerName + " (" + coord.x + "," + coord.y + ")";
        }

        public string getRouterName()
        {
            return routerName;
        }

        public Router neighbor(int dir)
        {
            int x, y;
            if (Config.bFtfly == true)
            {
                int cur_x=coord.x;
                int cur_y=coord.y;
                switch (dir)
                {
                    case Simulator.DIR_Y_0: x = cur_x; y = 0;break;
                    case Simulator.DIR_Y_1: x = cur_x; y = 1; break;
                    case Simulator.DIR_Y_2: x = cur_x; y = 2; break;
                    case Simulator.DIR_Y_3: x = cur_x; y = 3; break;
                    case Simulator.DIR_X_0: x = 0; y = cur_y; break;
                    case Simulator.DIR_X_1: x = 1; y = cur_y; break;
                    case Simulator.DIR_X_2: x = 2; y = cur_y; break;
                    case Simulator.DIR_X_3: x = 3; y = cur_y; break;
                    default: return null;
                }
            }
            else
            {
                switch (dir)
                {
                    case Simulator.DIR_UP: x = coord.x; y = coord.y + 1; break;
                    case Simulator.DIR_DOWN: x = coord.x; y = coord.y - 1; break;
                    case Simulator.DIR_RIGHT: x = coord.x + 1; y = coord.y; break;
                    case Simulator.DIR_LEFT: x = coord.x - 1; y = coord.y; break;
                    default: return null;
                }
                // mesh, not torus: detect edge
                if (x < 0 || x >= Config.network_nrX || y < 0 || y >= Config.network_nrY) return null;
            }

            return Simulator.network.routers[Coord.getIDfromXY(x, y)];
        }

        public void close()
        {
        }

        public virtual void visitFlits(Flit.Visitor fv)
        {
        }

        public void statsStarve(Flit f)
        {
            Simulator.stats.starve_flit.Add();
            Simulator.stats.starve_flit_bysrc[f.packet.src.ID].Add();

            if (last_starve == Simulator.CurrentRound - 1) {
              starve_interval++;
            } else {
              Simulator.stats.starve_interval_bysrc[f.packet.src.ID].Add(starve_interval);
              starve_interval = 0;
            }

            last_starve = Simulator.CurrentRound;
        }

        public int linkUtil()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
                if (linkIn[i] != null && linkIn[i].Out != null)
                    count++;
            return count;
        }

        public double linkUtilNeighbors()
        {
            int tot = 0, used = 0;
            for (int dir = 0; dir < 4; dir++)
            {
                Router n = neighbor(dir);
                if (n == null) continue;
                tot += n.neighbors;
                used += n.linkUtil();
            }

            return (double)used / tot;
        }

        public bool linkOutInputFree(int dir, out int pdDir)
        {
            pdDir = Simulator.DIR_NONE;
            if (linkOut[dir].In == null)
            {
                pdDir = dir;
                return true;
            }
            if (Config.bFtfly == true)
            {
                int freeDir = Simulator.DIR_X_0;
                int endRange = Simulator.DIR_X_3;;
                if (dir>=Simulator.DIR_Y_0)
                {
                    freeDir = Simulator.DIR_Y_0;
                    endRange = Simulator.DIR_Y_3;;
                }
                for (; freeDir<=endRange; freeDir++)
                {
                    // cannot have self-loop
                    if (freeDir == dir || freeDir == coord.x)
                        continue;
                    if (linkOut[freeDir] != null && linkOut[freeDir].In == null)
                    {
                        // change direction
                        pdDir = freeDir;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
