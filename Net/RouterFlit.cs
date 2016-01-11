//#define DEBUG
//#define DEBUG_SWAP
//#define RETX_DEBUG
//#define memD

using System;
using System.Collections.Generic;
using System.Text;

namespace ICSimulator
{
    public abstract class Router_Flit : Router
    {
        // injectSlot is from Node; injectSlot2 is higher-priority from
        // network-level re-injection (e.g., placeholder schemes)
        protected Flit m_injectSlot, m_injectSlot2;

        public Router_Flit(Coord myCoord)
            : base(myCoord)
        {
            m_injectSlot = null;
            m_injectSlot2 = null;
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
        protected void acceptFlit(Flit f)
        {
            statsEjectFlit(f);
            if (f.packet.nrOfArrivedFlits + 1 == f.packet.nrOfFlits)
                statsEjectPacket(f.packet);

            m_n.receiveFlit(f);
        }

        protected Flit ejectLocal()
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
            ret = handleGolden(ret);
        
            return ret;
        }

        protected Flit[] input = new Flit[INPUT_NUM]; // keep this as a member var so we don't
        // have to allocate on every step (why can't
        // we have arrays on the stack like in C?)

        protected override void _doStep()
        {
            Flit eject = ejectLocal();

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
            bool wantToInject = m_injectSlot2 != null || m_injectSlot != null;
            bool canInject = (c + outCount) < neighbors;
            bool starved = wantToInject && !canInject;

            if (starved)
            {
                Flit starvedFlit = null;
                if (starvedFlit == null) starvedFlit = m_injectSlot2;
                if (starvedFlit == null) starvedFlit = m_injectSlot;

                Simulator.controller.reportStarve(coord.ID);
                statsStarve(starvedFlit);
                Simulator.stats.starve_flit_throttle_bysrc[starvedFlit.packet.src.ID].Add();
            }
            if (canInject && wantToInject)
            {
                // With ACT, we throttle injection queue for control packets only.
                if(!Simulator.controller.ThrottleAtRouter || Simulator.controller.tryInject(coord.ID))
                {
                    Flit inj = null;
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
                    else
                        throw new Exception("what???inject null flits??");
                    input[c++] = inj;
#if DEBUG
                    Console.Write("Cycle: {0} PID: {1} FID: {2} | ",Simulator.CurrentRound,inj.packet.ID,
                            inj.flitNr);
                    int r=inj.packet.requesterID;
                    if(r==coord.ID)
                        Console.Write("INJECT flit at node ({0},{1})<>request:{2} #flits {3}",
                                coord.x,coord.y,r,inj.packet.nrOfFlits);
                    else
                        Console.Write("INJECT DIFF flit at node ({0},{1})<>request:{2} #flits {3}",
                                coord.x,coord.y,r,inj.packet.nrOfFlits);
                    Console.Write(" Dest at ({0},{1})\n",inj.dest.x,inj.dest.y);
#endif
                    statsInjectFlit(inj);
                }
            }

            if (eject != null)
                acceptFlit(eject);

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

#if DEBUG
                Console.WriteLine("Cycle: {2} PID: {0} FID: {1} | ARB coord ({3},{4})",
                        input[i].packet.ID, input[i].flitNr, Simulator.CurrentRound, coord.x, coord.y);
#endif
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
                    {
                        if (linkOut[dir] != null && linkOut[dir].In == null)
                        {
                            linkOut[dir].In = input[i];
                            outDir = dir;
                            break;
                        }
                    }

                    if (outDir == -1) throw new Exception(
                            String.Format("Ran out of outlinks in arbitration at node {0} on input {1} cycle {2} flit {3} c {4} neighbors {5} outcount {6}", coord, i, Simulator.CurrentRound, input[i], c, neighbors, outCount));
                }
            }
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

        public override void flush()
        {
            m_injectSlot = null;
        }

        protected virtual bool needFlush(Flit f) { return false; }
    }

    public class Router_Flit_OldestFirst : Router_Flit
    {
        public Router_Flit_OldestFirst(Coord myCoord)
            : base(myCoord)
        {
        }

        protected override bool needFlush(Flit f)
        {
            return Config.cheap_of_cap != -1 && age(f) > (ulong)Config.cheap_of_cap;
        }

        public static ulong age(Flit f)
        {
            if (Config.net_age_arbitration)
                return Simulator.CurrentRound - f.packet.injectionTime;
            else
                return (Simulator.CurrentRound - f.packet.creationTime) /
                        (ulong)Config.cheap_of;
        }

        public static int _rank_gpu(Flit f1, Flit f2)
        {
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

            int c1 = 0, c2 = 0;
            if (f1.packet != null && f2.packet != null)
            {
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



        public static int _rank(Flit f1, Flit f2)
        {
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

            int c1 = 0, c2 = 0;
            if (f1.packet != null && f2.packet != null)
            {
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

        public override int rank(Flit f1, Flit f2)
        {
            return _rank(f1, f2);
        }

        public override void visitFlits(Flit.Visitor fv)
        {
            if (m_injectSlot != null)
                fv(m_injectSlot);
            if (m_injectSlot2 != null)
                fv(m_injectSlot2);
        }
    }

    public class Router_Flit_Prio : Router_Flit
    {
        public Router_Flit_Prio(Coord myCoord)
            : base(myCoord)
        {
        }

        protected override bool needFlush(Flit f)
        {
            return Config.cheap_of_cap != -1 && age(f) > (ulong)Config.cheap_of_cap;
        }

        public static ulong age(Flit f)
        {
            if (Config.net_age_arbitration)
                return Simulator.CurrentRound - f.packet.injectionTime;
            else
                return (Simulator.CurrentRound - f.packet.creationTime) /
                        (ulong)Config.cheap_of;
        }

        public static int _rank(Flit f1, Flit f2)
        {
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

            int c1 = 0, c2 = 0;
            if (f1.packet != null && f2.packet != null)
            {
                //TODO: need to change here to take into account of the priority
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

        public override int rank(Flit f1, Flit f2)
        {
            return _rank(f1, f2);
        }

        public override void visitFlits(Flit.Visitor fv)
        {
            if (m_injectSlot != null)
                fv(m_injectSlot);
            if (m_injectSlot2 != null)
                fv(m_injectSlot2);
        }
    }

    /*
      Golden Packet is conceptually like this:
      
      for mshr in mshrs:
        for node in nodes:
          prioritize (node,mshr) request packet over all others, for L cycles

      where L = X+Y for an XxY network. All non-prioritized packets are arbitrated
      arbitrarily, if you will (say, round-robin).
    */

    public class Router_Flit_GP : Router_Flit
    {
        public Router_Flit_GP(Coord myCoord)
            : base(myCoord)
        {
        }

        public static int _rank(Flit f1, Flit f2)
        {
            // priority is:
            // 1. Carrier (break ties by dest ID)
            // 2. Rescuer (break ties by dest ID)
            // 3. Golden normal flits (break ties by flit no.)
            // 4. Non-golden normal flits (break ties arbitrarily)
            // 5. Placeholders

            if (f1 == null && f2 == null)
                return 0;
            if (f1 == null)
                return 1;
            if (f2 == null)
                return -1;

            if (f1.state == Flit.State.Carrier && f2.state == Flit.State.Carrier)
                return f1.dest.ID.CompareTo(f2.dest.ID);
            else if (f1.state == Flit.State.Carrier)
                return -1;
            else if (f2.state == Flit.State.Carrier)
                return 1;

            if (f1.state == Flit.State.Rescuer && f2.state == Flit.State.Rescuer)
                return f1.dest.ID.CompareTo(f2.dest.ID);
            else if (f1.state == Flit.State.Carrier)
                return -1;
            else if (f2.state == Flit.State.Carrier)
                return 1;

            if (f1.state == Flit.State.Normal && f2.state == Flit.State.Normal)
            {

                bool golden1 = Simulator.network.golden.isGolden(f1),
                     golden2 = Simulator.network.golden.isGolden(f2);
                
                bool silver1 = f1.isSilver,
                     silver2 = f2.isSilver;
                    
                bool defl1 = (Config.deflDePrio) ? f1.wasDeflected : false,
                     defl2 = (Config.deflDePrio) ? f2.wasDeflected : false;

                if (golden1 && golden2)
                {
                    int g1 = Simulator.network.golden.goldenLevel(f1),
                        g2 = Simulator.network.golden.goldenLevel(f2);

                    if (g1 != g2)
                        return g1.CompareTo(g2);
                    else
                        return f1.flitNr.CompareTo(f2.flitNr);
                }
                else if (golden1)
                    return -1;
                else if (golden2)
                    return  1;
                //else if (silver1 && silver2)
                //    throw new Exception("Only one flit should be silver at a time");
                else if (silver1)
                    return -1;
                else if (silver2)
                    return  1;
                else if (defl1 && defl2)
                    return (1 == Simulator.rand.Next(2)) ? -1 : 1;
                else if (defl1)
                    return  1;
                else if (defl2)
                    return -1;
                else
                    return (1 == Simulator.rand.Next(2)) ? -1 : 1;
                    
            }
            else if (f1.state == Flit.State.Normal)
                return -1;
            else if (f2.state == Flit.State.Normal)
                return 1;
            else
                // both are placeholders
                //return Simulator.rand.Next(3) - 1;
                return (Simulator.rand.Next(2) == 1) ? -1 : 1;
        }

        public override int rank(Flit f1, Flit f2)
        {
            return Router_Flit_GP._rank(f1, f2);
        }
    }

    public class Router_Flit_Random : Router_Flit
    {
        public Router_Flit_Random(Coord myCoord)
            : base(myCoord)
        {
        }

        public override int rank(Flit f1, Flit f2)
        {
            //return Simulator.rand.Next(3) - 1; // one of {-1,0,1}
            return Simulator.rand.Next(2); //one of {-1,1}
        }
    }

    public class Router_Flit_Exhaustive : Router_Flit
    {
        public Router_Flit_Exhaustive(Coord myCoord)
            : base(myCoord)
        {
        }

        protected override void _doStep()
        {
            int index;
            int bestPermutationProgress = -1;
            IEnumerable<int> bestPermutation = null;

            foreach (IEnumerable<int> thisPermutation in PermuteUtils.Permute<int>(new int[] { 0, 1, 2, 3, 4 }, 4))
            {
                index = 0;
                int thisPermutationProgress = 0;
                foreach (int direction in thisPermutation)
                {
                    Flit f = linkIn[index++].Out;
                    if (f == null)
                        continue;

                    if (direction == LOCAL_INDEX)
                    {
                        if (f.dest.ID == this.ID)
                            thisPermutationProgress++;
                        else
                            goto PermutationDone; // don't allow ejection of non-arrived flits
                    }
                    else if (isDirectionProductive(f.packet.dest, direction))
                        thisPermutationProgress++;
                    //Console.Write(" " + direction);
                }

                if (thisPermutationProgress > bestPermutationProgress)
                {
                    bestPermutation = thisPermutation;
                    bestPermutationProgress = thisPermutationProgress;
                }
            PermutationDone:
                continue;
            }

            index = 0;
            foreach (int direction in bestPermutation)
            {
                Flit f = linkIn[index++].Out;
                //Console.Write(" {1}->{0}", direction, (f == null ? "null" : f.dest.ID.ToString()));
                if (direction == LOCAL_INDEX)
                    this.acceptFlit(f);
                else
                {
                    if (f != null && !isDirectionProductive(f.packet.dest, direction))
                        f.Deflected = true;
                    linkOut[direction].In = f;
                }
            }
            //Console.WriteLine();
            //throw new Exception("Done!");
        }
    }

    public class Router_Flit_Ctlr : Router_Flit
    {
        public Router_Flit_Ctlr(Coord myCoord)
            : base(myCoord)
        {
        }

        public override int rank(Flit f1, Flit f2)
        {
            return Simulator.controller.rankFlits(f1, f2);
        }
    }

    public class Router_Flit_Ctlr_InjectPrio : Router_Flit
    {
        Link[] swapLink;

        public Router_Flit_Ctlr_InjectPrio(Coord myCoord)
            : base(myCoord)
        {
            swapLink = new Link[INPUT_NUM];

            for (int i = 0; i < INPUT_NUM; i++)
                swapLink[i] = new Link(0,0,0); // a single cycle link
        }

        protected override void _doStep()
        {
            for (int i = 0; i < INPUT_NUM; i++)
                swapLink[i].doStep();

            Flit eject = ejectLocal();

            for (int i = 0; i < INPUT_NUM; i++) input[i] = null;

            // grab inputs into a local array so we can sort
            int c = 0;
            for (int dir = 0; dir < TOTAL_DIR; dir++)
            {
                if (linkIn[dir] != null && linkIn[dir].Out != null)
                {
                    input[c++] = linkIn[dir].Out;
                    linkIn[dir].Out.inDir = dir;
                    linkIn[dir].Out = null;
                }
                /*else if (linkIn[dir] != null && linkIn[dir].Out == null && swapLink[dir] != null &&
                         swapLink[dir].Out != null)
                {
#if DEBUG_SWAP
                    Console.WriteLine("C SWAP BACK loop: {0} @ ({1},{2}) dir:{3} PID:{4}",
                                      Simulator.CurrentRound, coord.x, coord.y, dir,
                                      swapLink[dir].Out.packet.ID);
#endif

                    input[c++] = swapLink[dir].Out;
                    if (swapLink[dir].Out.inDir != dir)
                        throw new Exception("Unmatched direction.");
                    swapLink[dir].Out.inDir = dir;
                    swapLink[dir].Out = null;
                }*/
            }

            

            // sometimes network-meddling such as flit-injection can put unexpected
            // things in outlinks...
            int outCount = 0;
            for (int dir = 0; dir < TOTAL_DIR; dir++)
                if (linkOut[dir] != null && linkOut[dir].In != null)
                    outCount++;

            if (Config.bFtfly == true && neighbors != 6)
                 throw new Exception("ftfly router flit: WRONG NUMBER OF NEIGHBORS");
            bool wantToInject = m_injectSlot2 != null || m_injectSlot != null;
            bool canInject = (c + outCount) < neighbors;
            bool starved = wantToInject && !canInject;

            // Swap in flits that were swapped out before.
            if (canInject)
            {
                int[] occupyDir = new int[TOTAL_DIR]; 
                for (int i = 0; i < c; i++)
                {
                    occupyDir[input[i].inDir] = 1; 
                }

                for (int dir = 0; dir < TOTAL_DIR && (c + outCount < neighbors); dir++)
                {
                    //if (linkIn[dir] != null && linkIn[dir].Out == null && swapLink[dir] != null &&
                    //     swapLink[dir].Out != null)
                    if (occupyDir[dir] == 0 && swapLink[dir] != null && swapLink[dir].Out != null)
                    {
#if DEBUG_SWAP
                        Console.WriteLine("C SWAP BACK loop: {0} @ ({1},{2}) dir:{3} PID:{4}-Nr:{5}",
                                          Simulator.CurrentRound, coord.x, coord.y, dir,
                                          swapLink[dir].Out.packet.ID, swapLink[dir].Out.flitNr);
#endif
                        input[c++] = swapLink[dir].Out;
                        if (swapLink[dir].Out.inDir != dir)
                            throw new Exception("Unmatched direction.");
                        swapLink[dir].Out.inDir = dir;
                        swapLink[dir].Out = null;
                    }
                }
            }

            canInject = (c + outCount) < neighbors;
            // Loop back flits again it they didn't get swap back in
            for (int i = 0; i < INPUT_NUM; i++)
                swapLink[i].In = swapLink[i].Out;

            // Inject flits from injection queue
            if (starved)
            {
                Flit starvedFlit = null;
                if (starvedFlit == null) starvedFlit = m_injectSlot2;
                if (starvedFlit == null) starvedFlit = m_injectSlot;

                Simulator.controller.reportStarve(coord.ID);
                statsStarve(starvedFlit);
                Simulator.stats.starve_flit_throttle_bysrc[starvedFlit.packet.src.ID].Add();
            }

            if (canInject && wantToInject)
            {
                Flit inj = null;
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
                else
                    throw new Exception("what???inject null flits??");

                // With ACT, we throttle injection queue for control packets only, instead of a router level.
                if(!Simulator.controller.ThrottleAtRouter || Simulator.controller.tryInject(coord.ID))
                {
                    input[c++] = inj;
#if DEBUG
                    Console.Write("Cycle: {0} PID: {1} FID: {2} | ",Simulator.CurrentRound,inj.packet.ID,
                            inj.flitNr);
                    int r=inj.packet.requesterID;
                    if(r==coord.ID)
                        Console.Write("INJECT flit at node ({0},{1})<>request:{2} #flits {3}",
                                coord.x,coord.y,r,inj.packet.nrOfFlits);
                    else
                        Console.Write("INJECT DIFF flit at node ({0},{1})<>request:{2} #flits {3}",
                                coord.x,coord.y,r,inj.packet.nrOfFlits);
                    Console.Write(" Dest at ({0},{1})\n",inj.dest.x,inj.dest.y);
#endif
                    statsInjectFlit(inj);
                }
            }
            else if (!canInject && wantToInject && Simulator.controller.MPKI[coord.ID] <= Config.lowMPKIPrio)
            {
                // 1. swap back loop at iterating linkIn loop
                // 2. global prio on swap in flit
                // 3. Impose a single cycle delay on loop back

                Flit highFlit    = null;
                int  highFlitIdx = 0;
                
                // Find a flit that comes from high MPKI app and swap it with low MPKI app's flit
                for (int i = 0; i < c; i++) 
                {
                    //Console.WriteLine("input flit node mpki {0}",Simulator.controller.MPKI[input[i].packet.src.ID]);
                    if (input[i] != null && Simulator.controller.MPKI[input[i].packet.src.ID] > Config.highVictimMPKI && input[i].swapOutCount < Config.swapOutCountThreshold)
                    {
                        highFlit = input[i];
                        highFlitIdx = i;
                        break;
                    }
                }

                // SWAP
                if (highFlit != null && swapLink[highFlit.inDir].Out == null && 
                    swapLink[highFlit.inDir].In == null)
                {
                    Flit inj = null;
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
                    else
                        throw new Exception("what???inject null flits??");

#if DEBUG_SWAP
                    Console.WriteLine("C SWAP LOW: {0} @ ({1},{2}) dir:{3} PID:{4}-Nr:{7} outcount:{5} input count:{6}",
                                      Simulator.CurrentRound, coord.x, coord.y,
                                      highFlit.inDir, highFlit.packet.ID, outCount, c, highFlit.flitNr);
#endif
                    inj.inDir = highFlit.inDir;
                    input[highFlitIdx] = inj;
                    highFlit.swapOutCount++;
                    if (highFlit.swapOutCount > Config.swapOutCountThreshold)
                        throw new Exception("stuck in swap for too long");
                    swapLink[highFlit.inDir].In = highFlit;
                    statsInjectFlit(inj);
                }
            }

            if (eject != null)
                acceptFlit(eject);

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

#if DEBUG
                Console.WriteLine("Cycle: {2} PID: {0} FID: {1} | ARB coord ({3},{4}) dest ({5},{6})",
                        input[i].packet.ID, input[i].flitNr, Simulator.CurrentRound, coord.x, coord.y,
                        input[i].packet.dest.x, input[i].packet.dest.y);
#endif

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
                    {
                        if (linkOut[dir] != null && linkOut[dir].In == null)
                        {
                            linkOut[dir].In = input[i];
                            outDir = dir;
                            break;
                        }
                    }

                    if (outDir == -1) throw new Exception(
                            String.Format("Ran out of outlinks in arbitration at node {0} on input {1} cycle {2} flit {3} c {4} neighbors {5} outcount {6}", coord, i, Simulator.CurrentRound, input[i], c, neighbors, outCount));
                }
            }
        }

        public override int rank(Flit f1, Flit f2)
        {
            if (Config.bLowMPKIPrio)
            {
                bool f1Low = (Simulator.controller.MPKI[f1.packet.src.ID] <= Config.lowMPKIPrio) ? true : false;
                bool f2Low = (Simulator.controller.MPKI[f2.packet.src.ID] <= Config.lowMPKIPrio) ? true : false;

                if (f1Low && f2Low)
                    return 0;
                else if (f1Low && !f2Low)
                    return -1;
                else if (!f1Low && f2Low)
                    return 1;
                else
                    return Router_Flit_OldestFirst._rank(f1, f2);
            }
            else
                return Router_Flit_OldestFirst._rank(f1, f2);
        }

    }
}
