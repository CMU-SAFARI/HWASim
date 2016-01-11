//#define debug

using System;
using System.Collections.Generic;
using System.Text;

namespace ICSimulator
{
    public class AFCBufferSlot : IComparable
    {
        Flit m_f;
        public ulong inTimeStamp;

        public Flit flit { get { return m_f; } set { m_f = value; } }

        public AFCBufferSlot(Flit f)
        {
            m_f = f;
            inTimeStamp = Simulator.CurrentRound;
        }

        public int CompareTo(object o)
        {
            if (o is AFCBufferSlot)
            {
                if (Config.controller == ControllerType.STC)
                    return Simulator.controller.rankFlits(m_f, (o as AFCBufferSlot).m_f);
                return Router_Flit_OldestFirst._rank(m_f, (o as AFCBufferSlot).m_f);
            }
            else
                throw new ArgumentException("bad comparison");
        }

        public static ulong age(Flit f)
        {
            if (Config.net_age_arbitration)
                return Simulator.CurrentRound - f.packet.injectionTime;
            else
                return (Simulator.CurrentRound - f.packet.creationTime) /
                        (ulong)Config.cheap_of;
        }


        //Rachata: Flit Prioritization for GPU -- lower GPU priority
        public int CompareTo_GPU(object o)
        {
            if (o is AFCBufferSlot)
            {
                Simulator.stats.port_conflict.Add();
                Flit f1 = m_f;
                Flit f2 = (o as AFCBufferSlot).m_f;
    
                //Edit this --> copied from _rank
                if (f1 == null && f2 == null) return 0;
                if (f1 == null) return 1;
                if (f2 == null) return -1;
    
                bool f1_resc = (f1.state == Flit.State.Rescuer) || (f1.state == Flit.State.Carrier);
                bool f2_resc = (f2.state == Flit.State.Rescuer) || (f2.state == Flit.State.Carrier);
                bool f1_place = (f1.state == Flit.State.Placeholder);
                bool f2_place = (f2.state == Flit.State.Placeholder);
    
                int c0 = 0;
                if (f1_resc && f2_resc)
                    c0 = 0;
                else if (f1_resc)
                    c0 = -1;
                else if (f2_resc)
                    c0 = 1;
                else if (f1_place && f2_place)
                    c0 = 0;
                else if (f1_place)
                    c0 = 1;
                else if (f2_place)
                    c0 = -1;
    
#if debug
                if(f1!=null)
                    Console.WriteLine("CompareTo: f1 from_GPU = {0}",f1.packet.from_GPU);
                if(f2!=null)
                    Console.WriteLine("CompareTo: f2 from_GPU = {0}",f2.packet.from_GPU);
#endif

                int c1 = 0, c2 = 0;
                if (f1.packet != null && f2.packet != null)
                {
                    if(f1.packet.from_GPU && !f2.packet.from_GPU)
                    {
                        Simulator.stats.GPU_deprio.Add();
                        c1 = 1;
                    }
                    else if(!f1.packet.from_GPU && f2.packet.from_GPU)
                    {
                        Simulator.stats.GPU_deprio.Add();
                        c1 = -1;
                    }
                    else 
                        c1 = -age(f1).CompareTo(age(f2));
                    c2 = f1.packet.ID.CompareTo(f2.packet.ID);
                }
    
                int c3 = f1.flitNr.CompareTo(f2.flitNr);
    
                int zerosSeen = 0;
                foreach (int i in new int[] { c0, c1, c2, c3 })
                {
                    if (i == 0)
                        zerosSeen++;
                    else
                        break;
                }
                Simulator.stats.net_decisionLevel.Add(zerosSeen);
    
                return
                    (c0 != 0) ? c0 :
                    (c1 != 0) ? c1 :
                    (c2 != 0) ? c2 :
                    c3;
    
                }
                else
                    throw new ArgumentException("bad comparison");
        }

        public void getNewTimeStamp()
        {
            inTimeStamp = Simulator.CurrentRound;
        }
    }

    public class AFCUtilAvg
    {
        double m_avg;
        double m_window_sum;
        double[] m_window;
        int m_window_ptr;

        public AFCUtilAvg()
        {
            m_window = new double[Config.afc_avg_window];
            m_window_ptr = 0;
            m_window_sum = 0;
            m_avg = 0;
        }

        public void Add(double util)
        {
            // add new sample to window and update sum
            m_window_sum -= m_window[m_window_ptr];
            m_window[m_window_ptr] = util;
            m_window_sum += util;
            m_window_ptr = (m_window_ptr + 1) % Config.afc_avg_window;

            // mix window-average into EWMA
            m_avg = Config.afc_ewma_history * m_avg +
                (1 - Config.afc_ewma_history) * (m_window_sum / Config.afc_avg_window);
        }

        public double Avg { get { return m_avg; } }
    }

    public class Router_AFC : Router
    {
        // injectSlot is from Node
        protected Flit m_injectSlot;
        
        // buffers, indexed by physical channel and virtual network
        public MinHeap<AFCBufferSlot>[,] m_buf;
        public int m_buf_occupancy;

        // buffers active?
        public bool m_buffered;

        public Router_AFC(Coord myCoord)
            : base(myCoord)
        {
            m_injectSlot = null;

            m_buf = new MinHeap<AFCBufferSlot>[TOTAL_PORTS, Config.afc_vnets];
            for (int pc = 0; pc < TOTAL_PORTS; pc++)
                for (int i = 0; i < Config.afc_vnets; i++)
                    m_buf[pc, i] = new MinHeap<AFCBufferSlot>();

            m_buffered = false;
            m_buf_occupancy = 0;
        }

        public bool isBuffered { get { return m_buffered; } }

        public int totalBufCount()
        {
            return m_buf_occupancy;
        }

        public int fullBuffer()
        {
            int fullCount = 0;
            for (int pc = 0; pc < TOTAL_PORTS; pc++)
                for (int i = 0; i < Config.afc_vnets; i++)
                    if (m_buf[pc, i].Count == Config.afc_buf_per_vnet)
                        fullCount++;
            return fullCount;
        }

        public int totalBufCap()
        {
            return TOTAL_PORTS * Config.afc_vnets * Config.afc_buf_per_vnet;
        }

        protected Router_AFC getNeigh(int dir)
        {
            return neigh[dir] as Router_AFC;
        }

        // accept one ejected flit into rxbuf
        protected void acceptFlit(Flit f)
        {
            statsEjectFlit(f);
            if (f.packet.nrOfArrivedFlits + 1 == f.packet.nrOfFlits)
                statsEjectPacket(f.packet);

            m_n.receiveFlit(f);
        }

        Flit ejectLocal()
        {
            // eject locally-destined flit (highest-ranked, if multiple)
            Flit ret = null;
            int bestDir = -1;
            for (int dir = 0; dir < TOTAL_DIR; dir++)
                if (linkIn[dir] != null && linkIn[dir].Out != null &&
                        linkIn[dir].Out.state != Flit.State.Placeholder &&
                        linkIn[dir].Out.dest.ID == ID &&
                        (ret == null || rank(linkIn[dir].Out, ret) < 0))
                {
                    ret = linkIn[dir].Out;
                    bestDir = dir;
                }

            if (bestDir != -1) linkIn[bestDir].Out = null;
            return ret;
        }

        // keep these as member vars so we don't have to allocate on every step
        // (why can't we have arrays on the stack like in C?)
        Flit[] input = new Flit[INPUT_NUM]; 
        // With an extra local input
        AFCBufferSlot[] requesters = new AFCBufferSlot[TOTAL_PORTS];
        int[] requester_dir = new int[TOTAL_PORTS];

        Queue<AFCBufferSlot> m_freeAFCSlots = new Queue<AFCBufferSlot>();

        AFCBufferSlot getFreeBufferSlot(Flit f)
        {
            if (m_freeAFCSlots.Count > 0)
            {
                AFCBufferSlot s = m_freeAFCSlots.Dequeue();
                s.flit = f;
                s.getNewTimeStamp();
                return s;
            }
            else
                return new AFCBufferSlot(f);
        }
        void returnFreeBufferSlot(AFCBufferSlot s)
        {
            m_freeAFCSlots.Enqueue(s);
        }

        void switchBufferless()
        {
            m_buffered = false;
        }

        void switchBuffered()
        {
            m_buffered = true;
            if (m_injectSlot != null)
            {
                InjectFlit(m_injectSlot);
                m_injectSlot = null;
            }
        }

        AFCUtilAvg m_util_avg = new AFCUtilAvg();

        protected override void _doStep()
        {
            int flit_count = 0;
            for (int dir = 0; dir < TOTAL_DIR; dir++)
                if (linkIn[dir] != null && linkIn[dir].Out != null)
                    flit_count++;

            m_util_avg.Add((double)flit_count / neighbors);

            Simulator.stats.afc_avg.Add(m_util_avg.Avg);
            Simulator.stats.afc_avg_bysrc[ID].Add(m_util_avg.Avg);

            bool old_status = m_buffered;
            bool new_status = old_status;
            bool gossip_induced = false;

            if (Config.afc_force)
            {
                new_status = Config.afc_force_buffered;
            }
            else
            {
                if (!m_buffered && 
                        (m_util_avg.Avg > Config.afc_buf_threshold))
                    new_status = true;

                if (m_buffered &&
                        (m_util_avg.Avg < Config.afc_bless_threshold) &&
                        m_buf_occupancy == 0)
                    new_status = false;

                // check at least one free slot in downstream routers; if not, gossip-induced switch
                for (int n = 0; n < TOTAL_DIR; n++)
                {
                    Router_AFC nr = getNeigh(n);
                    if (nr == null) continue;
                    //neighbor's perspective of directoin on the current node.
                    int oppDir = Simulator.DIR_NONE;
                    if (Config.bFtfly)
                        oppDir = (n<Simulator.DIR_Y_0)?(coord.x):(Simulator.DIR_Y_0+coord.y);
                    else
                        oppDir = (n + 2) % 4;
                    if (oppDir == Simulator.DIR_NONE)
                        throw new Exception("AFC-Unable to determine neighbor's direction toward current node.");

                    for (int vnet = 0; vnet < Config.afc_vnets; vnet++)
                    {
                        int occupancy = nr.m_buf[oppDir, vnet].Count;
                        if ((capacity(vnet) - occupancy) < 2)
                        {
                            gossip_induced = true;
                            break;
                        }
                    }
                }
                if (gossip_induced) new_status = true;
            }

            // perform switching and stats accumulation
            if (old_status && !new_status)
            {
                switchBufferless();
                Simulator.stats.afc_switch.Add();
                Simulator.stats.afc_switch_bless.Add();
                Simulator.stats.afc_switch_bysrc[ID].Add();
                Simulator.stats.afc_switch_bless_bysrc[ID].Add();
            }
            if (!old_status && new_status)
            {
                switchBuffered();
                Simulator.stats.afc_switch.Add();
                Simulator.stats.afc_switch_buf.Add();
                Simulator.stats.afc_switch_bysrc[ID].Add();
                Simulator.stats.afc_switch_buf_bysrc[ID].Add();
            }

            if (m_buffered)
            {
                Simulator.stats.afc_buffered.Add();
                Simulator.stats.afc_buffered_bysrc[ID].Add();
                if (gossip_induced)
                {
                    Simulator.stats.afc_gossip.Add();
                    Simulator.stats.afc_gossip_bysrc[ID].Add();
                }
            }
            else
            {
                Simulator.stats.afc_bless.Add();
                Simulator.stats.afc_bless_bysrc[ID].Add();
            }

            if (m_buffered)
            {
                Simulator.stats.afc_buf_enabled.Add();
                Simulator.stats.afc_buf_enabled_bysrc[ID].Add();

                Simulator.stats.afc_buf_occupancy.Add(m_buf_occupancy);
                Simulator.stats.afc_buf_occupancy_bysrc[ID].Add(m_buf_occupancy);

#if debug
                Console.WriteLine("input buffer enqueue");
#endif
                // grab inputs into buffers
                for (int dir = 0; dir < TOTAL_DIR; dir++)
                {
                    if (linkIn[dir] != null && linkIn[dir].Out != null)
                    {
                        Flit f = linkIn[dir].Out;
                        linkIn[dir].Out = null;
                        AFCBufferSlot slot = getFreeBufferSlot(f);
                        m_buf[dir, f.packet.getClass()].Enqueue(slot);
                        m_buf_occupancy++;

                        Simulator.stats.afc_buf_write.Add();
                        Simulator.stats.afc_buf_write_bysrc[ID].Add();
                    }
                }

                // perform arbitration: (i) collect heads of each virtual-net
                // heap (which represents many VCs) to obtain a single requester
                // per physical channel; (ii)  request outputs among these
                // requesters based on DOR; (iii) select a single winner
                // per output

                for (int i = 0; i < TOTAL_PORTS; i++)
                {
                    requesters[i] = null;
                    requester_dir[i] = -1;
                }
                
#if debug
                Console.WriteLine("request enqueue");
#endif
                // find the highest-priority vnet head for each input PC
                for (int pc = 0; pc < TOTAL_PORTS; pc++)
                    for (int vnet = 0; vnet < Config.afc_vnets; vnet++)
                        if (m_buf[pc, vnet].Count > 0)
                        {
                            // Sanity check: self loop channel shouldn't have any flits
                            if (Config.bFtfly && (pc == coord.x || pc == 4+coord.y))
                                throw new Exception("Self-loop buffer should be empty!");
                            AFCBufferSlot top = m_buf[pc, vnet].Peek();
#if debug
                            Console.WriteLine("flit dest ({0},{1})",top.flit.dest.x,
                                            top.flit.dest.y);
#endif
                            PreferredDirection pd = determineDirection(top.flit, coord);
                            int outdir = (pd.xDir != Simulator.DIR_NONE) ?
                                pd.xDir : pd.yDir;
                            if (outdir == Simulator.DIR_NONE)
                                outdir = LOCAL_INDEX; // local ejection

                            // skip if (i) not local ejection and (ii)
                            // destination router is buffered and (iii)
                            // no credits left to destination router
                            if (outdir != LOCAL_INDEX)
                            {
                                Router_AFC nrouter = (Router_AFC)neigh[outdir];
                                int ndir =  (outdir + 2) % 4;
                                if (Config.bFtfly)
                                    ndir = (outdir<4)?(coord.x):(4+coord.y);
#if debug
                                Console.WriteLine("non local request check coord ({2},{3})-outdir {0} ndir {1}",
                                        outdir,ndir,coord.x,coord.y);
#endif
                                if (nrouter.m_buf[ndir, vnet].Count >= capacity(vnet) &&
                                        nrouter.m_buffered)
                                    continue;
                            }

#if debug
                            Console.WriteLine("contend req queue");
#endif
                            // otherwise, contend for top requester from this
                            // physical channel
                            if (requesters[pc] == null ||
                                    top.CompareTo(requesters[pc]) < 0)
                            {
                                requesters[pc] = top;
                                requester_dir[pc] = outdir;
                            }
                        }

#if debug
                Console.WriteLine("output arb");
#endif
                // find the highest-priority requester for each output, and pop
                // it from its heap
                for (int outdir = 0; outdir < TOTAL_PORTS; outdir++)
                {
                    // Self-loop ports that don't exist
                    if (Config.bFtfly && (outdir == coord.x || outdir == 4+coord.y))
                        continue;

                    AFCBufferSlot top = null;
                    AFCBufferSlot top2 = null;
                    int top_indir = -1;
                    int top2_indir = -1;
                    int nrWantToEject = 0;
                    
                    for (int req = 0; req < TOTAL_PORTS; req++)
                    {
                        // Self-loop ports that don't exist
                        if (Config.bFtfly && (req == coord.x || req == 4+coord.y))
                        {
                            if (requester_dir[req] != Simulator.DIR_NONE && 
                                requesters[req] != null)
                                throw new Exception("Error: Non-empty self-loop requester queue!");
                            continue;
                        }

#if debug
                        Console.WriteLine("output arb with dir {0} in req {1}",outdir,req);
#endif
                        int _dir = requester_dir[req];
                        //
                        // FBFLY Sanity check: make sure no inputs are going
                        // into self-loop non-existing dirs.
                        //
                        if (Config.bFtfly && (_dir == coord.x || _dir == 4+coord.y))
                            throw new Exception("Input flit enters self-loop ports!\n");

                        if (requesters[req] != null && _dir == outdir)
                        {
                            if (req == LOCAL_INDEX && top != null && requesters[req].CompareTo(top) > 0)
                                statsStarve(requesters[req].flit);

                            if (top == null ||
                                    requesters[req].CompareTo(top) < 0)
                            {
                                top2 = top;
                                top2_indir = top_indir;
                                top = requesters[req];
                                top_indir = req;
                                nrWantToEject++;
                            }
                        }
                    }

                    if (top_indir != -1)
                    {
                        m_buf[top_indir, top.flit.packet.getClass()].Dequeue();
                            
                        if (top.inTimeStamp == Simulator.CurrentRound)
                            Simulator.stats.buffer_bypasses.Add();

                        Simulator.stats.afc_buf_read.Add();
                        Simulator.stats.afc_buf_read_bysrc[ID].Add();
                        Simulator.stats.afc_xbar.Add();
                        Simulator.stats.afc_xbar_bysrc[ID].Add();

                        if (top_indir == LOCAL_INDEX)
                            statsInjectFlit(top.flit);

                        // propagate to next router (or eject)
                        if (outdir == LOCAL_INDEX)
                            acceptFlit(top.flit);
                        else
                        {
                            linkOut[outdir].In = top.flit;
                        //Rachata: Per-link stat
                            Simulator.stats.link_used[(this.ID*(TOTAL_PORTS-1)+outdir)].Add();
                        }

                        returnFreeBufferSlot(top);
                        m_buf_occupancy--;
                    }

                    if (Config.ejectCount == 2 && outdir == LOCAL_INDEX && top2_indir != -1)
                    {
                        m_buf[top2_indir, top2.flit.packet.getClass()].Dequeue();    
                        if (top2 != null)
                            acceptFlit(top2.flit);
                        
                        returnFreeBufferSlot(top2);
                        m_buf_occupancy--;
                    }
                }
            }
            else
            {
                //Flit eject = ejectLocal();
                Flit[] eject = new Flit[Config.ejectCount];
                int ejCount = 0;
                for (int i = 0; i < Config.ejectCount; i++)
                {
                    eject[ejCount] = ejectLocal();
                    ejCount++;
                }

                for (int i = 0; i < INPUT_NUM; i++) input[i] = null;

                // grab inputs into a local array so we can sort
                int c = 0;
                for (int dir = 0; dir < TOTAL_DIR; dir++)
                    if (linkIn[dir] != null && linkIn[dir].Out != null)
                    {
                        input[c++] = linkIn[dir].Out;
                        linkIn[dir].Out.inDir = dir;
                        linkIn[dir].Out = null;
                    }

                // sometimes network-meddling such as flit-injection can put unexpected
                // things in outlinks...
                int outCount = 0;
                for (int dir = 0; dir < TOTAL_DIR; dir++)
                    if (linkOut[dir] != null && linkOut[dir].In != null)
                        outCount++;

                if (Config.bFtfly == true && neighbors != 6)
                     throw new Exception("ftfly router flit: WRONG NUMBER OF NEIGHBORS");
                bool wantToInject = m_injectSlot != null;
                bool canInject = (c + outCount) < neighbors;
                bool starved = wantToInject && !canInject;

                if (starved)
                {
                    Flit starvedFlit = m_injectSlot;
                    Simulator.controller.reportStarve(coord.ID);
                    statsStarve(starvedFlit);
                }
                if (canInject && wantToInject)
                {
                    Flit inj = null;
                    if (m_injectSlot != null)
                    {
                        inj = m_injectSlot;
                        m_injectSlot = null;
                    }
                    else
                        throw new Exception("trying to inject a null flit");

                    input[c++] = inj;

                    statsInjectFlit(inj);
                }

                //if (eject != null)
                //    acceptFlit(eject);
                for (int i = 0; i < Config.ejectCount; i++)
                {
                    if (eject[i] != null)
                        acceptFlit(eject[i]);
                }

                // inline bubble sort is faster for this size than Array.Sort()
                // sort input[] by descending priority. rank(a,b) < 0 iff a has higher priority.
                for (int i = 0; i < INPUT_NUM; i++)
                    for (int j = i + 1; j < INPUT_NUM; j++)
                        if (input[j] != null &&
                                (input[i] == null ||
                                 rank(input[j], input[i]) < 0))
                        {
                            Flit t = input[i];
                            input[i] = input[j];
                            input[j] = t;
                        }

                // assign outputs
                for (int i = 0; i < INPUT_NUM && input[i] != null; i++)
                {
                    PreferredDirection pd = determineDirection(input[i], coord);
                    int outDir = -1;
                    int freeDir = -1;

                    //
                    // For mesh, only 1 direction is the desired direction, but for
                    // flattened butterfly, all directions on the same axis are valid.
                    // However, fbfly considers the direct express link first.
                    //
                    if (pd.xDir != Simulator.DIR_NONE && linkOutInputFree(pd.xDir, out freeDir) == true)
                    {
                        if (freeDir == Simulator.DIR_NONE)
                            throw new Exception("Broken linkout function.");
                        linkOut[freeDir].In = input[i];
                        outDir = freeDir;
                    }
                    else if (pd.yDir != Simulator.DIR_NONE && linkOutInputFree(pd.yDir, out freeDir) == true)
                    {
                        if (freeDir == Simulator.DIR_NONE)
                            throw new Exception("Broken linkout function.");
                        linkOut[freeDir].In = input[i];
                        outDir = freeDir;
                    }
                    // deflect!
                    else
                    {
                        input[i].Deflected = true;
                        int dir = 0;
                        if (Config.randomize_defl)
                        {
                            do
                            {
                                dir = Simulator.rand.Next(TOTAL_DIR); // randomize deflection dir (so no bias)
                            }while (Config.bFtfly == true && (dir == coord.x || dir == coord.y));
                        }

                        for (int count = 0; count < TOTAL_DIR; count++, dir = (dir + 1) % TOTAL_DIR)
                            if (linkOut[dir] != null && linkOut[dir].In == null)
                            {
                                linkOut[dir].In = input[i];
                                outDir = dir;
                                break;
                            }

                        if (outDir == -1) throw new Exception(
                                String.Format("Ran out of outlinks in arbitration at node {0} on input {1} cycle {2} flit {3} c {4} neighbors {5} outcount {6}", coord, i, Simulator.CurrentRound, input[i], c, neighbors, outCount));
                    }
                }
            }
        }

        public override bool canInjectFlit(Flit f)
        {
            int cl = f.packet.getClass();

            if (m_buffered)
                return m_buf[LOCAL_INDEX, cl].Count < capacity(cl);
            else
                return m_injectSlot == null;
        }

        public override void InjectFlit(Flit f)
        {
            if (Config.afc_real_classes)
                Simulator.stats.afc_vnet[f.packet.getClass()].Add();
            else
                Simulator.stats.afc_vnet[this.ID].Add();

            if (m_buffered)
            {
                AFCBufferSlot slot = getFreeBufferSlot(f);
                m_buf[LOCAL_INDEX, f.packet.getClass()].Enqueue(slot);
                m_buf_occupancy++;

                Simulator.stats.afc_buf_write.Add();
                Simulator.stats.afc_buf_write_bysrc[ID].Add();
            }
            else
            {
                if (m_injectSlot != null)
                    throw new Exception("Trying to inject twice in one cycle");

                m_injectSlot = f;
            }
        }

        int capacity(int cl)
        {
            // in the future, we might size each virtual network differently; for now,
            // we use just one virtual network (since there is no receiver backpressure)
            return Config.afc_buf_per_vnet;
        }

        public override void flush()
        {
            m_injectSlot = null;
        }

        protected virtual bool needFlush(Flit f) { return false; }
    }
}
