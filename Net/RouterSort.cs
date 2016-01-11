//#define DEBUG
using System;
using System.Collections.Generic;

namespace ICSimulator
{
    public class SortNode
    {
        public delegate int Steer(Flit f);
        public delegate int Rank(Flit f1, Flit f2);

        Steer m_s;
        Rank m_r;

        public Flit in_0, in_1, out_0, out_1;

        public SortNode(Steer s, Rank r)
        {
            m_s = s;
            m_r = r;
        }

        public void doStep()
        {
            Flit winner, loser;

            if (m_r(in_0, in_1) < 0)
            {
                winner = in_0;
                loser = in_1;
            }
            else
            {
                loser = in_0;
                winner = in_1;
            }

            if (winner != null) winner.sortnet_winner = true;
            if (loser != null) loser.sortnet_winner = false;

            int dir = m_s(winner);

            if (dir == 0)
            {
                out_0 = winner;
                out_1 = loser;
            }
            else
            {
                out_0 = loser;
                out_1 = winner;
            }
        }
    }

    public abstract class SortNet
    {
        public Flit loopBackX = null;
        public Flit loopBackY = null;
        public abstract void route(Flit[] input, out bool injected);
    }

    // NOTE: This is "4x4" specific.
    public class SortNet_CALF_FBFLY : SortNet
    {
        ResubBuffer rbuf;
        int rb_inject_block_count;
        SortNode[] nodes;
        int LOCAL_INDEX;
        int TOTAL_DIR;
        Coord coord;

        public SortNet_CALF_FBFLY(SortNode.Rank r, Router router)
        {
            // 4x4 sorting network
            nodes = new SortNode[12];

            // Domain 1 (x-axis) goes 0. Domain 2 (y-axis) goes 1.
            SortNode.Steer crossDomain = delegate(Flit f)
            {
                if (f == null)
                    return 0;
                return (f.prefDir <= Simulator.DIR_X_3)?0:1;
            };

            // within first half domain, goes 0; otherwise goes 1.
            SortNode.Steer intraDomain = delegate(Flit f)
            {
                if (f == null)
                    return 0;
                return (f.prefDir%4 < 2)?0:1;
            };

            // even channel number (e.g. DIR_X_0) goes 0; otherwise goes 1.
            SortNode.Steer evenSteer = delegate(Flit f)
            {
                if (f == null)
                    return 0;
                return (f.prefDir%2 == 0)?0:1;
            };
           
            for (int i = 0; i < 4; i++)
            {
                nodes[i]   = new SortNode(crossDomain, r);
                nodes[i+4] = new SortNode(intraDomain, r);
                nodes[i+8] = new SortNode(evenSteer, r);
            }

            LOCAL_INDEX = Router.LOCAL_INDEX;
            TOTAL_DIR   = Router.TOTAL_DIR;
            coord       = router.coord;
            loopBackX   = null;
            loopBackY   = null;
            rbuf        = new ResubBuffer();
            rb_inject_block_count = 0;
        }
        
        protected bool rb_inject(Flit[] input)
        {
            bool redirection = false;
            Flit temp = null;
            
            if (rbuf.isEmpty())
            {
                rb_inject_block_count = 0;
            }

            if (rb_inject_block_count > Config.redirectThreshold)
            {
                redirection = true;
            }

            if (redirection)
            {
                int rand = Simulator.rand.Next(TOTAL_DIR);
                
                if (input[rand] != null && !Simulator.network.golden.isGolden(input[rand]))
                {
                    temp = input[rand];
                    input[rand] = null;
                    rb_inject_block_count = 0;
                }
            }
            
            int injectedCount = 0;
            // Resubmit Buffer Injection
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (rbuf.isEmpty() || injectedCount == Config.rbInjectCount)
                    break;
                
                if (input[i] == null)
                {
                    input[i] = rbuf.removeFlit();
                    injectedCount++;
                    Simulator.stats.rb_injectCount.Add();
                }
            }
            
            if (!rbuf.isEmpty() && injectedCount == 0)
                rb_inject_block_count++;

            if (redirection && temp != null)
            {
                Simulator.stats.redirectedFlits.Add();
                Simulator.stats.rb_ejectCount.Add();
                rbuf.addFlit(temp);
                
            }
            return redirection;
        }

        protected void rb_eject(Flit[] input, bool redirection)
        {
            int[] index = new int[TOTAL_DIR];
            int j = 0;
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null && input[i].prefDir != i)
                {
                    input[i].wasDeflected = true;
                    if (redirection)
                    {
                        Simulator.stats.rb_isRedirection.Add();
                        continue;
                    }

                    if(Simulator.network.golden.isGolden(input[i]))
                        continue;
                    
                    if (input[i].currentX == input[i].dest.x && input[i].currentY == input[i].dest.y)
                        continue;
                   
                    index[j] = i;
                    j++;
                }
            }
            
            if (!Config.resubmitBuffer || j == 0 || redirection)
                return;

            int rand = Simulator.rand.Next(j);
            if (!rbuf.isFull())
            {
                Simulator.stats.resubmittedFlits.Add();
                rbuf.addFlit(input[index[rand]]);
                input[index[rand]] = null;
                //input[index[rand]].rb_inTime = Simulator.currentCycle
            }
        }

        public override void route(Flit[] input, out bool injected)
        {
            // Loop back flits that didn't have any productive output ports
            int x = coord.x;
            int y = coord.y + Simulator.DIR_Y_0;
            if (!Config.sortnetDeflect)
            {
                input[x] = loopBackX;
                input[y] = loopBackY;
            }

            injected = false;
            bool redirection = false;

            if (Config.resubmitBuffer)
                redirection = rb_inject(input);

            if (!Config.calf_new_inj_ej)
            {
                // injection: if free slot, insert flit
                if (input[LOCAL_INDEX] != null)
                {
                    // Make sure it's not looking at non-existing inputs
                    for (int i = 0; i < TOTAL_DIR; i++)
                        if (input[i] == null && i!=coord.x && i!=(coord.y+Simulator.DIR_Y_0))
                        {
                            input[i] = input[LOCAL_INDEX];
                            injected = true;
                            break;
                        }

                    input[LOCAL_INDEX] = null;
                }
            }

#if DEBUG
            Console.WriteLine("Coord ({0},{1}) @ {2}\nSorting network: available ports->",coord.x,coord.y, Simulator.CurrentRound);
            for (int i = 0;i <TOTAL_DIR; i++)
                if (input[i]!=null)
                    Console.Write("{0} ",i);
            Console.Write("\n");
#endif

            // Silver flit logic
            int[] flitPositions = new int[TOTAL_DIR];
            int   flitCount     = 0;

            // Finds non-null flits and puts indicies in an array
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null)
                {
                    flitPositions[flitCount] = i;
                    flitCount++;
                    input[i].isSilver = false;
                }
            }

            // Picks a random flit out of that and makes it silver
            if (Config.silverFlit && flitCount != 0)
            {
                int randNum = flitPositions[Simulator.rand.Next(flitCount)];
                input[randNum].isSilver = true;
            }

            bool flag = false;
            int sI = 0;
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null && input[i].isSilver)
                {
                    if (flag)
                        throw new Exception(String.Format("Double silver flit at {0} w/ previous at {1}",i,sI));
                    flag = true;
                    sI = i;
                }
            }
            // SILVERFLIT ENDS

            if (Config.sortnetInterleave)
            {
                nodes[0].in_0 = input[Simulator.DIR_X_0];
                nodes[0].in_1 = input[Simulator.DIR_Y_0];
                nodes[1].in_0 = input[Simulator.DIR_X_1];
                nodes[1].in_1 = input[Simulator.DIR_Y_1];
                nodes[2].in_0 = input[Simulator.DIR_X_2];
                nodes[2].in_1 = input[Simulator.DIR_Y_2];
                nodes[3].in_0 = input[Simulator.DIR_X_3];
                nodes[3].in_1 = input[Simulator.DIR_Y_3];
            }
            else
            {
                nodes[0].in_0 = input[Simulator.DIR_X_0];
                nodes[0].in_1 = input[Simulator.DIR_X_1];
                nodes[1].in_0 = input[Simulator.DIR_X_2];
                nodes[1].in_1 = input[Simulator.DIR_X_3];
                nodes[2].in_0 = input[Simulator.DIR_Y_0];
                nodes[2].in_1 = input[Simulator.DIR_Y_1];
                nodes[3].in_0 = input[Simulator.DIR_Y_2];
                nodes[3].in_1 = input[Simulator.DIR_Y_3];
            }

            nodes[0].doStep();
            nodes[1].doStep();
            nodes[2].doStep();
            nodes[3].doStep();

            nodes[4].in_0 = nodes[0].out_0;
            nodes[4].in_1 = nodes[2].out_0;
            nodes[5].in_0 = nodes[1].out_0;
            nodes[5].in_1 = nodes[3].out_0;
            nodes[6].in_0 = nodes[0].out_1;
            nodes[6].in_1 = nodes[2].out_1;
            nodes[7].in_0 = nodes[1].out_1;
            nodes[7].in_1 = nodes[3].out_1;

            nodes[4].doStep();
            nodes[5].doStep();
            nodes[6].doStep();
            nodes[7].doStep();


            nodes[8].in_0 = nodes[4].out_0;
            nodes[8].in_1 = nodes[5].out_0;
            nodes[9].in_0 = nodes[4].out_1;
            nodes[9].in_1 = nodes[5].out_1;
            nodes[10].in_0 = nodes[6].out_0;
            nodes[10].in_1 = nodes[7].out_0;
            nodes[11].in_0 = nodes[6].out_1;
            nodes[11].in_1 = nodes[7].out_1;

            nodes[8].doStep();
            nodes[9].doStep();
            nodes[10].doStep();
            nodes[11].doStep();

            input[0] = nodes[8].out_0;
            input[1] = nodes[8].out_1;
            input[2] = nodes[9].out_0;
            input[3] = nodes[9].out_1;
            input[4] = nodes[10].out_0;
            input[5] = nodes[10].out_1;
            input[6] = nodes[11].out_0;
            input[7] = nodes[11].out_1;


            if (input[x] != null)
            {
                if (Config.sortnetDeflect)
                {
                    for (int i = 0; i < TOTAL_DIR; i++)
                    {
                        if (input[i] == null && i!=x && i!=y)
                        {
                            input[i] = input[x];
                            break;
                        }
                    }
#if DEBUG
                    Console.WriteLine("self-loop x {0}->",x);
#endif
                }
                else //loopback
                {
                    loopBackX = input[x];
                }
                input[x] = null;
            }
            else
            {
                loopBackX = null;
            }

            if (input[y] != null)
            {
                if (Config.sortnetDeflect)
                {
                    for (int i = 0; i < TOTAL_DIR; i++)
                    {
                        if (input[i] == null && i!=x && i!=y)
                        {
                            input[i] = input[y];
                            break;
                        }
                    }
#if DEBUG
                    Console.WriteLine("self-loop y {0}->",y);
#endif
                }
                else //loopback
                {
                    loopBackY = input[y];
                }
                input[y] = null;
            }
            else
            {
                loopBackY = null;
            }
        
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null && input[i].prefDir != i)
                {
                    Simulator.stats.rb_totalDeflected.Add();
                    input[i].wasDeflected = true;
                }
            }

            //Resubmit Buffer ejection
            rb_eject(input, redirection);

            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null && input[i].prefDir != i)
                {
                    Simulator.stats.rb_deflected.Add();
                }
            }
        }
    }

    public class SortNet_CALF : SortNet
    {
        SortNode[] nodes;
        ResubBuffer rbuf;
        int TOTAL_DIR;
        int rb_inject_block_count;

        public SortNet_CALF(SortNode.Rank r)
        {
            nodes = new SortNode[4];

            SortNode.Steer stage1_steer = delegate(Flit f)
            {
                if (f == null)
                    return 0;

                return (f.prefDir == Simulator.DIR_UP || f.prefDir == Simulator.DIR_DOWN) ?
                    0 : // NS switch
                    1;  // EW switch
            };
           
            // node 0: {N,E} -> {NS, EW}
            nodes[0] = new SortNode(stage1_steer, r);
            // node 1: {S,W} -> {NS, EW}
            nodes[1] = new SortNode(stage1_steer, r);

            // node 2: {in_top,in_bottom} -> {N,S}
            nodes[2] = new SortNode(delegate(Flit f)
                    {
                        if (f == null) return 0;
                        return (f.prefDir == Simulator.DIR_UP) ? 0 : 1;
                    }, r);
            // node 3: {in_top,in_bottm} -> {E,W}
            nodes[3] = new SortNode(delegate(Flit f)
                    {
                        if (f == null) return 0;
                        return (f.prefDir == Simulator.DIR_RIGHT) ? 0 : 1;
                    }, r);

            rbuf = new ResubBuffer();
            TOTAL_DIR = 4;
            rb_inject_block_count = 0;
        }

        // takes Flit[5] as input; indices DIR_{UP,DOWN,LEFT,RIGHT} and 4 for local.
        // permutes in-place. input[4] is left null; if flit was injected, 'injected' is set to true.
        public override void route(Flit[] input, out bool injected)
        {
            injected = false;
            bool redirection = false;

            if (Config.resubmitBuffer)
                redirection = rb_inject(input);

            if (!Config.calf_new_inj_ej)
            {
                // injection: if free slot, insert flit
                if (input[4] != null)
                {
                    for (int i = 0; i < 4; i++)
                        if (input[i] == null)
                        {
                            input[i] = input[4];
                            injected = true;
                            break;
                        }

                    input[4] = null;
                }
            }
            

            // Silver flit logic
            int[] flitPositions = new int[4];
            int   flitCount     = 0;

            // Finds non-null flits and puts indicies in an array
            for (int i = 0; i < 4; i++)
            {
                if (input[i] != null)
                {
                    flitPositions[flitCount] = i;
                    flitCount++;
                    input[i].isSilver = false;
                }
            }

            // Picks a random flit out of that and makes it silver
            if (Config.silverFlit && flitCount != 0)
            {
                int randNum = flitPositions[Simulator.rand.Next(flitCount)];
                input[randNum].isSilver = true;
            }

            if (Config.sortnet_twist)
            {
                nodes[0].in_0 = input[Simulator.DIR_UP];
                nodes[0].in_1 = input[Simulator.DIR_RIGHT];
                nodes[1].in_0 = input[Simulator.DIR_DOWN];
                nodes[1].in_1 = input[Simulator.DIR_LEFT];
            }
            else
            {
                nodes[0].in_0 = input[Simulator.DIR_UP];
                nodes[0].in_1 = input[Simulator.DIR_DOWN];
                nodes[1].in_0 = input[Simulator.DIR_LEFT];
                nodes[1].in_1 = input[Simulator.DIR_RIGHT];
            }
            nodes[0].doStep();
            nodes[1].doStep();
            nodes[2].in_0 = nodes[0].out_0;
            nodes[2].in_1 = nodes[1].out_0;
            nodes[3].in_0 = nodes[0].out_1;
            nodes[3].in_1 = nodes[1].out_1;
            nodes[2].doStep();
            nodes[3].doStep();
            input[Simulator.DIR_UP] = nodes[2].out_0;
            input[Simulator.DIR_DOWN] = nodes[2].out_1;
            input[Simulator.DIR_RIGHT] = nodes[3].out_0;
            input[Simulator.DIR_LEFT] = nodes[3].out_1;

            rb_eject(input, redirection);
        }

        protected bool rb_inject(Flit[] input)
        {
            bool redirection = false;
            Flit temp = null;
            
            if (rbuf.isEmpty())
            {
                rb_inject_block_count = 0;
            }

            if (rb_inject_block_count > Config.redirectThreshold)
            {
                Simulator.stats.redirectionCount.Add();
                redirection = true;
            }

            if (redirection)
            {
                int rand = Simulator.rand.Next(TOTAL_DIR) - 1;
                
                if (input[rand] != null && !Simulator.network.golden.isGolden(input[rand]))
                {
                    temp = input[rand];
                    rb_inject_block_count = 0;
                }
            }
            
            int injectedCount = 0;
            // Resubmit Buffer Injection
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (rbuf.isEmpty() || injectedCount == Config.rbInjectCount)
                    break;
                
                if (input[i] == null)
                {
                    input[i] = rbuf.removeFlit();
                    injectedCount++;
                    Simulator.stats.rb_ejectCount.Add();
                }
            }
            
            if (!rbuf.isEmpty() && injectedCount == 0)
                rb_inject_block_count++;

            if (redirection && temp != null)
                rbuf.addFlit(temp);

            return redirection;
        }

        protected void rb_eject(Flit[] input, bool redirection)
        {
            if (redirection)
                return;

            int[] index = new int[TOTAL_DIR];
            int j = 0;
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null && input[i].prefDir != i)
                {
                    input[i].wasDeflected = true;
                    
                    if(Simulator.network.golden.isGolden(input[i]))
                        continue;
                    
                    if (input[i].currentX == input[i].dest.x && input[i].currentY == input[i].dest.y)
                        continue;
                   
                    index[j] = i;
                    j++;
                }
            }
            
            if (!Config.resubmitBuffer || j == 0)
                return;

            int rand = Simulator.rand.Next(j) - 1;
            if (!rbuf.isFull())
            {
                rbuf.addFlit(input[index[rand]]);
            }
        }
    }

    public class SortNet_COW : SortNet // Cheap Ordered Wiring?
    {
        SortNode[] nodes;

        public SortNet_COW(SortNode.Rank r)
        {
            nodes = new SortNode[6];

            SortNode.Steer stage1_steer = delegate(Flit f)
            {
                if (f == null) return 0;
                return (f.sortnet_winner) ? 0 : 1;
            };

            SortNode.Steer stage2_steer = delegate(Flit f)
            {
                if (f == null) return 0;
                return (f.prefDir == Simulator.DIR_UP || f.prefDir == Simulator.DIR_RIGHT) ?
                    0 : 1;
            };
           
            nodes[0] = new SortNode(stage1_steer, r);
            nodes[1] = new SortNode(stage1_steer, r);

            nodes[2] = new SortNode(stage2_steer, r);
            nodes[3] = new SortNode(stage2_steer, r);

            nodes[4] = new SortNode(delegate(Flit f)
                    {
                        if (f == null) return 0;
                        return (f.prefDir == Simulator.DIR_UP) ? 0 : 1;
                    }, r);
            nodes[5] = new SortNode(delegate(Flit f)
                    {
                        if (f == null) return 0;
                        return (f.prefDir == Simulator.DIR_DOWN) ? 0 : 1;
                    }, r);
        }

        // takes Flit[5] as input; indices DIR_{UP,DOWN,LEFT,RIGHT} and 4 for local.
        // permutes in-place. input[4] is left null; if flit was injected, 'injected' is set to true.
        public override void route(Flit[] input, out bool injected)
        {
            injected = false;

            if (!Config.calf_new_inj_ej)
            {
                // injection: if free slot, insert flit
                if (input[4] != null)
                {
                    for (int i = 0; i < 4; i++)
                        if (input[i] == null)
                        {
                            input[i] = input[4];
                            injected = true;
                            break;
                        }

                    input[4] = null;
                }
            }

            // NS, EW -> NS, EW
            if (!Config.sortnet_twist)
            {
                nodes[0].in_0 = input[Simulator.DIR_UP];
                nodes[0].in_1 = input[Simulator.DIR_RIGHT];
                nodes[1].in_0 = input[Simulator.DIR_DOWN];
                nodes[1].in_1 = input[Simulator.DIR_LEFT];
            }
            else
            {
                nodes[0].in_0 = input[Simulator.DIR_UP];
                nodes[0].in_1 = input[Simulator.DIR_DOWN];
                nodes[1].in_0 = input[Simulator.DIR_LEFT];
                nodes[1].in_1 = input[Simulator.DIR_RIGHT];
            }
            nodes[0].doStep();
            nodes[1].doStep();
            nodes[2].in_0 = nodes[0].out_0;
            nodes[3].in_0 = nodes[1].out_0;
            nodes[3].in_1 = nodes[0].out_1;
            nodes[2].in_1 = nodes[1].out_1;
            nodes[2].doStep();
            nodes[3].doStep();
            nodes[4].in_0 = nodes[2].out_0;
            nodes[4].in_1 = nodes[3].out_0;
            nodes[5].in_0 = nodes[2].out_1;
            nodes[5].in_1 = nodes[3].out_1;
            nodes[4].doStep();
            nodes[5].doStep();
            input[Simulator.DIR_UP] = nodes[4].out_0;
            input[Simulator.DIR_RIGHT] = nodes[4].out_1;
            input[Simulator.DIR_DOWN] = nodes[5].out_0;
            input[Simulator.DIR_LEFT] = nodes[5].out_1;
        }
    }

    public abstract class Router_SortNet : Router
    {
        // injectSlot is from Node; injectSlot2 is higher-priority from
        // network-level re-injection (e.g., placeholder schemes)
        protected Flit m_injectSlot, m_injectSlot2;

        SortNet m_sort;

        public Router_SortNet(Coord myCoord)
            : base(myCoord)
        {
            m_injectSlot = null;
            m_injectSlot2 = null;

            if (Config.sortnet_full)
                m_sort = new SortNet_COW(new SortNode.Rank(rank));
            else
            {
                if (Config.bFtfly)
                    m_sort = new SortNet_CALF_FBFLY(new SortNode.Rank(rank), this);
                else
                    m_sort = new SortNet_CALF(new SortNode.Rank(rank));
            }

            if (!Config.edge_loop)
                throw new Exception("SortNet (CALF) router does not support mesh without edge loop. Use -edge_loop option.");
        }

        Flit handleGolden(Flit f)
        {
            if (f == null)
                return f;

            if (f.state == Flit.State.Normal)
                return f;

            if (f.state == Flit.State.Rescuer)
            {
                if (m_injectSlot == null)
                {
                    m_injectSlot = f;
                    f.state = Flit.State.Placeholder;
                }
                else
                    m_injectSlot.state = Flit.State.Carrier;

                return null;
            }

            if (f.state == Flit.State.Carrier)
            {
                f.state = Flit.State.Normal;
                Flit newPlaceholder = new Flit(null, 0);
                newPlaceholder.state = Flit.State.Placeholder;

                if (m_injectSlot != null)
                    m_injectSlot2 = newPlaceholder;
                else
                    m_injectSlot = newPlaceholder;

                return f;
            }

            if (f.state == Flit.State.Placeholder)
                throw new Exception("Placeholder should never be ejected!");

            return null;
        }

        // accept one ejected flit into rxbuf
        void acceptFlit(Flit f)
        {
            statsEjectFlit(f);
            if (f.packet.nrOfArrivedFlits + 1 == f.packet.nrOfFlits)
                statsEjectPacket(f.packet);

            m_n.receiveFlit(f);
        }

        Flit ejectLocal()
        {
#if DEBUG
            Console.WriteLine("ejectlocal");
#endif 
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

            // Eject from loopback channels if using fbfly topology
            if (Config.bFtfly)
            {
                Flit loopBackX = m_sort.loopBackX;
                Flit loopBackY = m_sort.loopBackY;
                bool bLoopX = false;
                bool bLoopY = false;

                if (loopBackX != null &&
                    loopBackX.state != Flit.State.Placeholder &&
                    loopBackX.dest.ID == ID &&
                    (ret == null || rank(loopBackX, ret) < 0))
                {
                    ret = loopBackX;
                    bestDir = -1;
                    bLoopX = true;
                    bLoopY = false;
                }
                if (loopBackY != null &&
                    loopBackY.state != Flit.State.Placeholder &&
                    loopBackY.dest.ID == ID &&
                    (ret == null || rank(loopBackX, ret) < 0))
                {
                    ret = loopBackY;
                    bestDir = -1;
                    bLoopX = false;
                    bLoopY = true;
                }

                if (bLoopX)
                    m_sort.loopBackX = null;
                if (bLoopY)
                    m_sort.loopBackY = null;
            }

            if (bestDir != -1) linkIn[bestDir].Out = null;
#if DEBUG
            if (ret != null)
                Console.WriteLine("cyc {2} @ node ({3},{4}) ejecting flit ID:{0}.{1}", ret.packet.ID, ret.flitNr, Simulator.CurrentRound,coord.x,coord.y);
#endif
            ret = handleGolden(ret);

            return ret;
        }

        Flit[] m_ej = new Flit[TOTAL_DIR];
        int m_ej_rr = 0;
        Flit ejectLocalNew()
        {
            for (int dir = 0; dir < TOTAL_DIR; dir++)
                if (linkIn[dir] != null && linkIn[dir].Out != null &&
                    linkIn[dir].Out.dest.ID == ID &&
                    m_ej[dir] == null)
                {
                    m_ej[dir] = linkIn[dir].Out;
                    linkIn[dir].Out = null;
                }

            m_ej_rr++; 
            m_ej_rr %= TOTAL_DIR;

            Flit ret = null;
            if (m_ej[m_ej_rr] != null)
            {
                ret = m_ej[m_ej_rr];
                m_ej[m_ej_rr] = null;
            }

            return ret;
        }

        /* keep this as a member var so we don't
         have to allocate on every step (why can't
         we have arrays on the stack like in C?)*/
        Flit[] input = new Flit[TOTAL_PORTS];
        // Includes injection channel and 2 extra channels that are not used.

        protected override void _doStep()
        {
#if DEBUG
            Console.WriteLine("INITIAL dostep");
#endif 
            Flit[] eject = new Flit[Config.ejectCount];

            for (int i = 0; i < Config.ejectCount; i++)
            {
	            if (Config.calf_new_inj_ej)
                    eject[i] = ejectLocalNew();
                else
                    eject[i] = ejectLocal();
#if DEBUG
                Console.WriteLine("eject");
#endif 
            }
#if DEBUG
            Console.WriteLine("after ejection");
#endif 

            for (int dir = 0; dir < TOTAL_DIR; dir++)
            {
                if (linkIn[dir] != null && linkIn[dir].Out != null)
                {
                    input[dir] = linkIn[dir].Out;
                    input[dir].inDir = dir;
                }
                else
                    input[dir] = null;
            }

            Flit inj = null;
            bool injected = false;

            if (m_injectSlot2 != null)
            {
                inj = m_injectSlot2;
                m_injectSlot2 = null;
            }
            else if (m_injectSlot != null)
            {
                inj = m_injectSlot;
                m_injectSlot = null;
            }

#if DEBUG
            if (inj != null)
                Console.WriteLine("cyc {0} @ coord ({1},{2}) inject flit w/ PID: {3}.{4}", Simulator.CurrentRound,
                        coord.x,coord.y, inj.packet.ID, inj.flitNr);
#endif
            input[LOCAL_INDEX] = inj;
            if (inj != null)
                inj.inDir = -1;
            
            for (int i = 0; i < TOTAL_PORTS; i++)
            {
                if (input[i] != null)
                {
                    PreferredDirection pd = determineDirection(input[i]);
                    if (pd.xDir != Simulator.DIR_NONE)
                        input[i].prefDir = pd.xDir;
                    else
                        input[i].prefDir = pd.yDir;
                    //
                    // Flattened butterfly optimization - if a flit doesn't get to win ejection, forward it
                    // to the loopback port, instead of making it deflect to some random port.
                    //
                    if (Config.bFtfly && Config.bDeflectLoopback)
                    {
                        if (input[i].prefDir == Simulator.DIR_NONE)
                        {
                            Flit loopBackX = m_sort.loopBackX;
                            Flit loopBackY = m_sort.loopBackY;
                            if (loopBackX == null)
                            {
                                m_sort.loopBackX = input[i];
                                input[i] = null;
                            }
                            else if (loopBackY == null)
                            {
                                m_sort.loopBackY = input[i];
                                input[i] = null;
                            }
                        }
                    }
                }
            }

#if DEBUG
            Console.WriteLine("INITIAL route");
#endif 
            m_sort.route(input, out injected);

            // Basically, inputs becomes outputs after routing in the sorting
            // network.
            for (int i = 0; i < TOTAL_DIR; i++)
            {
                if (input[i] != null)
                {
#if DEBUG
                    Console.WriteLine("cyc {4} @ coord ({5},{6}) input dir {0} pref dir {1} output dir {2} age {3}",
                            input[i].inDir, input[i].prefDir, i, Router_Flit_OldestFirst.age(input[i]), Simulator.CurrentRound,
                            coord.x, coord.y);
#endif
                    input[i].Deflected = input[i].prefDir != i;
                }
            }

            if (Config.calf_new_inj_ej)
            {
                if (inj != null && input[inj.prefDir] == null)
                {
                    input[inj.prefDir] = inj;
                    injected = true;
                }
            }

            if (!injected)
            {
                if (m_injectSlot == null)
                    m_injectSlot = inj;
                else
                    m_injectSlot2 = inj;
            }
            else
                statsInjectFlit(inj);

            for (int i = 0; i < Config.ejectCount; i++)
            {
                if (eject[i] == null)
                    break;
                else
                    acceptFlit(eject[i]);
            }

            for (int dir = 0; dir < TOTAL_DIR; dir++)
                if (input[dir] != null)
                {
#if DEBUG
                    Console.WriteLine("cyc {0} @ coord ({1},{2})->arb output dir:{3} w/ ID: {4}",Simulator.CurrentRound,
                            coord.x, coord.y, dir, input[dir].packet.ID);
#endif
                    if (linkOut[dir] == null)
                        throw new Exception(String.Format("router {0} does not have link in dir {1}",
                                    coord, dir));
                    linkOut[dir].In = input[dir];
                }
#if DEBUG
            Console.WriteLine("-END OF DOSTEP-");
#endif
        }

        public override bool canInjectFlit(Flit f)
        {
            return m_injectSlot == null;
        }

        public override void InjectFlit(Flit f)
        {
            if (m_injectSlot != null)
                throw new Exception("Trying to inject twice in one cycle");

            m_injectSlot = f;
        }

        public override void visitFlits(Flit.Visitor fv)
        {
            if (m_injectSlot != null)
                fv(m_injectSlot);
            if (m_injectSlot2 != null)
                fv(m_injectSlot2);
        }
        //protected abstract int rank(Flit f1, Flit f2);
    }

    public class Router_SortNet_GP : Router_SortNet
    {
        public Router_SortNet_GP(Coord myCoord)
            : base(myCoord)
        {
        }

        public override int rank(Flit f1, Flit f2)
        {
            return Router_Flit_GP._rank(f1, f2);
        }
    }

    public class Router_SortNet_OldestFirst : Router_SortNet
    {
        public Router_SortNet_OldestFirst(Coord myCoord)
            : base(myCoord)
        {
        }

        public override int rank(Flit f1, Flit f2)
        {
            return Router_Flit_OldestFirst._rank(f1, f2);
        }
    }
}
