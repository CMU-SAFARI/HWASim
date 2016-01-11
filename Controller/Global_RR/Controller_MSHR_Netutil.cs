/**
 * @brief  
 * **/ 

//#define debug
//#define debugEstimate
//#define debugInjection
//#define debugIndividualThroughput

using System;
using System.Collections.Generic;

namespace ICSimulator
{

    public class Controller_MSHR_Netutil_Throttle : Controller_ClassicBLESS
    {
        public double[] throttleRate = new double[Config.N];
        IPrioPktPool[] m_injPools = new IPrioPktPool[Config.N];

        ulong[] ejectCount = new ulong[Config.N];
        ulong[] prevEjectCount = new ulong[Config.N];
        double[] ejectRate = new double[Config.N];

        ulong[] replyEjectCount = new ulong[Config.N];
        ulong[] replyPrevEjectCount = new ulong[Config.N];
        double[] replyEjectRate = new double[Config.N];
        
        bool[] throttlePermitted = new bool[Config.N];
        double[] _lastNetutil = new double[Config.N];
        double[] _lastEjectRate = new double[Config.N];

        int[] _mshrsWatermark = new int[Config.N];
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
        public Controller_MSHR_Netutil_Throttle()
        {
            for (int i = 0; i < Config.N; i++)
            {
                L1misses[i] = 0;
                MPKI[i]     = 0.0;
                throttleRate[i]  = 0.0;
                num_ins_last_epoch[i] = 0;
                throttlePermitted[i] = true;
                _mshrsWatermark[i] = 0; 
            }
        }

        public override void doStep()
        {
            if (((Simulator.CurrentRound % Config.ejectMonitorPeriod) == 0) && Simulator.CurrentRound != 0)
            {
                gatherInfoAndThrottle();
                resetStat();
            }
        }

        // Gather each node's state: MPKI, MPKC, and total average MPKC. 
        void gatherInfoAndThrottle()
        {
            double systemIPC    = 0.0;
            ulong  insnsRetired = 0;
            double sumMPKC = 0.0;
            double sumMPKI = 0.0;

            Console.WriteLine("\n");
            Console.WriteLine("Clock {0}", Simulator.CurrentRound);
            Console.WriteLine("Netutil {0:F3}.", Simulator.stats.netutil);

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

                // Reply ejection
                ulong replyEjCount = Simulator.stats.eject_flit_myReply[i].Count;

                // EjectCount within the last epoch
                ejectCount[i] = ejCount - prevEjectCount[i];
                replyEjectCount[i] = replyEjCount - replyPrevEjectCount[i];

                // Stats reset
                if (ejCount < prevEjectCount[i])
                    ejectCount[i] = ejCount;
                if (replyEjCount < replyPrevEjectCount[i])
                    replyEjectCount[i] = replyEjCount;

                // call throttle here so that we can compare with previous ejection count
                throttleNode(i);
               
                // Reset previous 
                prevEjectCount[i] = ejCount;
                replyPrevEjectCount[i] = replyEjCount;

                double ejRate = (double)ejectCount[i] / Config.ejectMonitorPeriod;
                double replyEjRate = (double)replyEjectCount[i] / Config.ejectMonitorPeriod;
                ejectRate[i] = ejRate;
                replyEjectRate[i] = replyEjRate;

                if (ejRate > Config.ejectRateLowThreshold)
                {
#if debug
                    Console.WriteLine("cycle {0} @", Simulator.CurrentRound);
                    Console.WriteLine("Netutil {0}", Simulator.network._cycle_netutil);
                    writeNode(i);
                    Console.WriteLine("-> MPKC: {0}", MPKC[i]);
                    Console.WriteLine("-> All Eject flits: {0} Eject rate: {1}", ejectCount[i], ejRate);
                    Console.WriteLine("-> RE  Eject flits: {0} Eject rate: {1}", replyEjectCount[i], replyEjRate);
#endif
                }
            }
            
            //Console.WriteLine("Sum MPKI {0} MPKC {1} Avg MPKC {2}", sumMPKI, sumMPKC, sumMPKC / Config.N);
        }
        
        bool _dropWithinRange(double compBase, double comp, double range)
        {
            if (compBase < comp)
                return false;
            return ((compBase - comp) / compBase) > range;
        }

        public void throttleNode(int i)
        {
            double _prevEjRate = _lastEjectRate[i];
            double _ejRate     = (double)replyEjectCount[i] / Config.ejectMonitorPeriod;

            //if (Simulator.stats.netutil_byreqID[i].Avg == 0.0 || _ejRate == 0.0)
            //{
            //    Config.mshrsWatermark[i] = 0;
            //    return;
            //}

            bool bNetutilDrop = _dropWithinRange(_lastNetutil[i], Simulator.stats.netutil_byreqID[i].Avg,
                                                 Config.netutilRange);
            bool bEjectDrop    = _dropWithinRange(_prevEjRate, _ejRate, Config.replyEjRange);

            writeNode(i);
            Console.WriteLine("Netutil drop {0:F3}->{1:F3}::{2}", _lastNetutil[i],
                                                            Simulator.stats.netutil_byreqID[i].Avg, bNetutilDrop);
            Console.WriteLine("Ejection drop {0:F3}->{1:F3}::{2}", _prevEjRate, _ejRate, bEjectDrop);

            if (Simulator.stats.netutil_byreqID[i].Avg == 0.0 || _ejRate == 0.0)
            {
                Config.mshrsWatermark[i] = 0;
                return;
            }

            _lastEjectRate[i] = _ejRate;
            _lastNetutil[i] = Simulator.stats.netutil_byreqID[i].Avg;
            Simulator.stats.netutil_byreqID[i].Reset();

            ///////////////////////////////////////////////////////////////////////
            /// Watermark adjustment
            ///////////////////////////////////////////////////////////////////////

            int watermark = _mshrsWatermark[i];
            
            if (bNetutilDrop == false && bEjectDrop == false)
                if (watermark != Config.mshrs)
                    watermark++;
            else if (bNetutilDrop == true && bEjectDrop == false)
                if (watermark != Config.mshrs)
                    watermark++;
            else if (bNetutilDrop == true && bEjectDrop == true)
                watermark--;
            else if (bNetutilDrop == false && bEjectDrop == true)
                watermark = 0;

            if (watermark > 0)
            {
                Config.mshrsWatermark[i] = watermark;
                _mshrsWatermark[i] = watermark;
            }
            Console.WriteLine("*****-> MSHRs: {0}", Config.mshrs - watermark);
            Simulator.stats.resize_mshrs_persrc[i].Add(Config.mshrs - watermark);
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
