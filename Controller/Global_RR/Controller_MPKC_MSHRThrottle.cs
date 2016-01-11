/**
 * @brief Simply changes MSHRs based on MPKC.
 **/

#define debug

using System;
using System.Collections.Generic;

namespace ICSimulator
{

    public class Controller_MPKC_MSHRThrottle : Controller_ClassicBLESS
    {
        public double[] throttleRate = new double[Config.N];
        IPrioPktPool[] m_injPools = new IPrioPktPool[Config.N];

        ulong[] ejectCount = new ulong[Config.N];
        ulong[] prevEjectCount = new ulong[Config.N];
        double[] ejectRate = new double[Config.N];

        ulong[] replyEjectCount = new ulong[Config.N];
        ulong[] replyPrevEjectCount = new ulong[Config.N];
        double[] replyEjectRate = new double[Config.N];

        /*
        /* Default controller uses one single queue. However, we are trying
        /* completely shut off injection of control packets only. Therefore
        /* we need to use separate queues for each control, data, & writeback.
        */
        public override IPrioPktPool newPrioPktPool(int node)
        {
            return MultiQThrottlePktPool.construct();
        }

        public override void setInjPool(int node, IPrioPktPool pool)
        {
            m_injPools[node] = pool;
            pool.setNodeId(node);
        }

        public override void resetStat()
        {
            for (int i = 0; i < Config.N; i++)
            {
                num_ins_last_epoch[i] = Simulator.stats.insns_persrc[i].Count;
                L1misses[i]=0;
            }
        }

        /*** Throttling ***/
        //
        // True to allow injection, false to block (throttle)
        // RouterFlit uses this function to determine whether it can inject or
        // not iff we are throttling at router leve. In ACT, and other
        // throttling projects, we are throttling the injection of reqest
        // queue.
        //
        public override bool tryInject(int node)
        {
            if (Simulator.rand.NextDouble() < throttleRate[node])
            {
                Simulator.stats.throttled_counts_persrc[node].Add();
                return false;
            }
            else
            {
                Simulator.stats.not_throttled_counts_persrc[node].Add();
                return true;
            }
        }

        /*** Constructor ***/
        public Controller_MPKC_MSHRThrottle()
        {
            for (int i = 0; i < Config.N; i++)
            {
                L1misses[i] = 0;
                MPKI[i]     = 0.0;
                throttleRate[i]  = 0.0;
                num_ins_last_epoch[i] = 0;
            }
        }

        public override void doStep()
        {
            if ((Simulator.CurrentRound % Config.ejectMonitorPeriod) == 0)
            {
                throttle();
                resetStat();
            }
        }

        // Gather each node's state: MPKI, MPKC, and total average MPKC.
        void gatherInfo()
        {
            double systemIPC    = 0.0;
            ulong  insnsRetired = 0;
            double sumMPKC = 0.0;
            double sumMPKI = 0.0;

            Console.WriteLine("cycle {0} @", Simulator.CurrentRound);
            Console.WriteLine("Netutil {0}", Simulator.network._cycle_netutil);

            // get the MPKI value
            for (int i = 0; i < Config.N; i++)
            {
                insnsRetired = Simulator.stats.every_insns_persrc[i].Count - num_ins_last_epoch[i];;
                if (insnsRetired < 0)
                    throw new Exception("Error gathering instructions!");

                prev_MPKI[i] = MPKI[i];
                MPKI[i] = (insnsRetired == 0) ? 0 : ((double)(L1misses[i]*1000)) / insnsRetired;
                systemIPC += ((double)insnsRetired) / Config.ejectMonitorPeriod;
                MPKC[i] = (double)(L1misses[i]*1000) / Config.ejectMonitorPeriod;
                sumMPKC += MPKC[i];
                sumMPKI += MPKI[i];

                // Get the ejection flit count within the last monitor period
                ulong ejCount = Simulator.stats.eject_flit_bydest[i].Count;

                ulong replyEjCount = Simulator.stats.eject_flit_myReply[i].Count;
                if (Config.ejectByReqID)
                    replyEjCount = Simulator.stats.eject_flit_byreqID[i].Count;

                ejectCount[i] = ejCount - prevEjectCount[i];
                replyEjectCount[i] = replyEjCount - replyPrevEjectCount[i];

                // Stats reset
                if (ejCount < prevEjectCount[i])
                    ejectCount[i] = ejCount;
                if (replyEjCount < replyPrevEjectCount[i])
                    replyEjectCount[i] = replyEjCount;

                prevEjectCount[i] = ejCount;
                replyPrevEjectCount[i] = replyEjCount;

                double ejRate = (double)ejectCount[i] / Config.ejectMonitorPeriod;
                double replyEjRate = (double)replyEjectCount[i] / Config.ejectMonitorPeriod;
                ejectRate[i] = ejRate;
                replyEjectRate[i] = replyEjRate;

#if debug
                writeNode(i);
                Console.WriteLine("-> MPKC: {0} ejrate {1}", MPKC[i], ejectRate[i]);
#endif
            }

            //Console.WriteLine("Sum MPKI {0} MPKC {1} Avg MPKC {2}", sumMPKI, sumMPKC, sumMPKC / Config.N);
        }

        public void throttle()
        {
            gatherInfo();

            // Set MSHR water mark (to resize the mshrs) based on MPKC
            for (int i = 0; i < Config.N; i++)
            {
                double mpkc = MPKC[i];

                int watermark = Config.mshrsWatermark[i];
                // Decrease 1 available MSHR
                if (mpkc >= 50)
                {
                    if (watermark < Config.mshrs)
                        watermark++;
                }
                else if (mpkc <= 40)
                {
                    if (watermark > 0)
                        watermark--;
                }

                if (watermark > 0)
                {
#if debug
                    writeNode(i);
                    Console.WriteLine("\n***** Watermark: {0}",watermark);
#endif
                    Config.mshrsWatermark[i] = watermark;
                }
                Simulator.stats.resize_mshrs_persrc[i].Add(Config.mshrs - watermark);
            }
        }

        string getName(int ID)
        {
            return Simulator.network.workload.getName(ID);
        }
        void writeNode(int ID)
        {
            Console.Write("{0} ({1}) ", ID, getName(ID));
        }
    }
}
