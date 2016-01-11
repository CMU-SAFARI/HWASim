/**
 * @brief Based on the ejection rate of each node, we dynamically changes the
 * availability of MSHRs. The insight is developed from the fact that when
 * ejection rate saturates, there is no benefit allocating more MSHR to
 * increase the number of outstanding requests and in fact it actually hurts
 * other applications by introducing interference.
 **/ 

//#define debugRate
//#define debug
#define hillClimb 
//#define debugEstimate
//#define debugInjection
//#define debugIndividualThroughput

using System;
using System.Collections.Generic;

namespace ICSimulator
{

    public class Controller_MSHR_Throttle : Controller_ClassicBLESS
    {
        public double[] throttleRate = new double[Config.N];
        IPrioPktPool[] m_injPools    = new IPrioPktPool[Config.N];

        ulong[] ejectCount     = new ulong[Config.N];
        ulong[] prevEjectCount = new ulong[Config.N];
        double[] ejectRate     = new double[Config.N];

        ulong[] replyEjectCount     = new ulong[Config.N];
        ulong[] replyPrevEjectCount = new ulong[Config.N];
        double[] replyEjectRate     = new double[Config.N];

        ulong[] m_injCount     = new ulong[Config.N];
        ulong[] m_prevInjCount = new ulong[Config.N];
        double[] m_injRate     = new double[Config.N];

        double m_lastThroughput = 0.0;
        double m_maxThroughput  = 0.0;
        double m_maxTarget      = 0.0;
        int    m_resetCount     = 0;

        double m_initialTarget;

        AveragingWindow avg_ipc, m_avg_L1_misses;
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
        public Controller_MSHR_Throttle()
        {
            for (int i = 0; i < Config.N; i++)
            {
                L1misses[i] = 0;
                MPKI[i]     = 0.0;
                throttleRate[i]  = 0.0;
                num_ins_last_epoch[i] = 0;
            }
            
            avg_ipc = new AveragingWindow((int)Config.hillClimbQuantum);
            m_avg_L1_misses = new AveragingWindow((int)Config.hillClimbQuantum);
            m_initialTarget = Config.mshrTh_netutil_target;
        }

        public override void doStep()
        {
            avg_ipc.accumulate((double)Simulator.network._cycle_insns / Config.N);
            m_avg_L1_misses.accumulate((double)(Simulator.network._cycle_L1_misses));

            if ((Simulator.CurrentRound % Config.ejectMonitorPeriod) == 0)
            {
                throttle();
                //monitor();
                resetStat();
            }
            if (Config.bHillClimb && (Simulator.CurrentRound % Config.hillClimbQuantum) == 0)
            {
                climbMountain();
            }
        }

        // Gather each node's state: MPKI, MPKC, and total average MPKC. 
        void gatherInfo()
        {
            double systemIPC    = 0.0;
            ulong  insnsRetired = 0;
            double sumMPKC = 0.0;
            double sumMPKI = 0.0;

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
                ulong injCount = Simulator.stats.mshrThrottle_inject_flit_bysrc[i].Count;
                ulong replyEjCount = Simulator.stats.eject_flit_myReply[i].Count;
                if (Config.ejectByReqID)
                    replyEjCount = Simulator.stats.eject_flit_byreqID[i].Count;

                ejectCount[i] = ejCount - prevEjectCount[i];
                replyEjectCount[i] = replyEjCount - replyPrevEjectCount[i];
                m_injCount[i] = injCount - m_prevInjCount[i];

                // Stats reset
                if (ejCount < prevEjectCount[i])
                    ejectCount[i] = ejCount;
                if (replyEjCount < replyPrevEjectCount[i])
                    replyEjectCount[i] = replyEjCount;
                if (injCount < m_prevInjCount[i]) 
                    m_injCount[i] = injCount;

                // Set new previous count
                prevEjectCount[i] = ejCount;
                replyPrevEjectCount[i] = replyEjCount;
                m_prevInjCount[i] = injCount;

                // Rate
                double ejRate = (double)ejectCount[i] / Config.ejectMonitorPeriod;
                double replyEjRate = (double)replyEjectCount[i] / Config.ejectMonitorPeriod;
                ejectRate[i] = ejRate;
                replyEjectRate[i] = replyEjRate;
                m_injRate[i] = (double)m_injCount[i] / Config.ejectMonitorPeriod;

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
        
        public void monitor()
        {
            gatherInfo();

            Console.WriteLine("Total netutil {0} Flit Net latency {1} -> Throughput {2}", 
                              Simulator.stats.mshrThrottle_netutil.Avg, 
                              Simulator.stats.mshrThrottle_flit_net_latency.Avg, 
                              (double) Simulator.stats.mshrThrottle_netutil.Avg / Simulator.stats.mshrThrottle_flit_net_latency.Avg);
            Simulator.stats.mshrThrottle_netutil.Reset();
            Simulator.stats.mshrThrottle_flit_net_latency.Reset();
            
            Console.WriteLine("\n");
            for (int i = 0; i < Config.N; i++)
            {
#if debugIndividualThroughput
                writeNode(i);
                Console.WriteLine("Individual netutil {0} Flit Net latency {1} -> Throughput {2}", 
                                  Simulator.stats.netutil_byreqID[i].Avg, 
                                  Simulator.stats.flit_net_latency_byreqID[i].Avg, 
                                  (double) Simulator.stats.netutil_byreqID[i].Avg / Simulator.stats.flit_net_latency_byreqID[i].Avg);
                Simulator.stats.netutil_byreqID[i].Reset();
                Simulator.stats.flit_net_latency_byreqID[i].Reset();
#endif
                /* 
                 * Find out the alone injection rate over the monitored
                 * period. */
#if debugInjection
                double injRate = (double)Simulator.stats.mshrThrottle_inject_flit_bysrc[i].Count / Config.ejectMonitorPeriod;
                // Reset for the next period
                Simulator.stats.mshrThrottle_inject_flit_bysrc[i].Reset();
                writeNode(i);
                Console.WriteLine("-> Injection rate {0}", injRate);
#endif
            }
        }
        
        // Hill climb net utilization to find the maximum throughput = netutil / net latency
        public void climbMountain()
        {
            double _netutil = Simulator.stats.mshrThrottle_netutil.Avg;
            double _netLat  = Simulator.stats.mshrThrottle_flit_net_latency.Avg;
            if (Config.bClimbIPC && Config.bClimbMPKC)
                throw new Exception("Want to climb both IPC and MPKC. WHICH ONE THEN?");
            double _throughput = (Config.bClimbIPC) ? avg_ipc.average() : _netutil / _netLat;
            if (Config.bClimbMPKC)
                _throughput = m_avg_L1_misses.average() * 1000; // MPKC
            double _target = Config.mshrTh_netutil_target;

#if hillClimb
            Console.WriteLine("Last throughput {0} :: New throughput {1}", m_lastThroughput, _throughput);
#endif

            // Record the maximum throughput and target pair
            if (_throughput > m_maxThroughput)
            {
                m_maxThroughput = _throughput;
                m_maxTarget = _target;
            }
            // If throughput drops too much, reset hill
            else if ((m_maxThroughput - _throughput) / m_maxThroughput > Config.resetHillThreshold)
            {
                m_resetCount++;

                _target = m_maxTarget;
                // If reset count exceeds some threshold, start finding max again
                if (m_resetCount >= 3 && Config.bResetCount)
                {
                    m_maxThroughput = 0.0;
                    m_maxTarget     = 0.0;
                    m_resetCount = 0;
                    _target = m_initialTarget;
#if hillClimb
                    Console.WriteLine("Reset to find new max throughput.");
#endif
                }
#if hillClimb
                Console.WriteLine("Reset netutil target {0}:: max th {1}", _target, m_maxThroughput);
#endif
                goto Reset;
            }
            // Make sure it's consecutive reset
            m_resetCount = 0;

            if (_throughput > 0)
            {
                if (_throughput >= m_lastThroughput)
                {
                    if ((_target - Config.decStep) > 0)
                        _target -= Config.decStep;
                }
                else if ((m_lastThroughput - _throughput) / m_lastThroughput >= Config.dropThreshold)
                {
                    if ((_target + Config.incStep) < 1.0)
                        _target += Config.incStep;
                }
            }
#if hillClimb
            Console.WriteLine("old target {0} -> new target {1}", Config.mshrTh_netutil_target, _target);
#endif

        Reset:
            Config.mshrTh_netutil_target = _target;
            m_lastThroughput = _throughput;
            // Reset for next quantum stats collection
            Simulator.stats.mshrThrottle_netutil.Reset();
            Simulator.stats.mshrThrottle_flit_net_latency.Reset();

            Simulator.stats.netutilTarget.Add(_target);
        }

        public void throttle()
        {
#if debugRate
            Console.WriteLine("\nCycle @ {0}", Simulator.CurrentRound);
#endif
            gatherInfo();

            // Dynamically configure the throttle threshold based on the
            // network utilization
            if (Config.dynamicThreshold)
            {
                double _netutil = (Config.router.algorithm == RouterAlgorithm.DR_AFC) ?  
                                  Simulator.stats.mshrThrottle_afc_total_util.Avg :
                                  Simulator.stats.mshrThrottle_smallEpoch_netutil.Avg;
#if hillClimb
                Console.WriteLine("Netutil {0}.", _netutil);
#endif
                double ejHighThresh = Config.ejectRateHighThreshold;
                double ejLowThresh = Config.ejectRateLowThreshold;
                if (_netutil > Config.mshrTh_netutil_target)
                {
                    if (ejLowThresh-0.05 > Config.ejectRateLowerBound)
                    {
                        ejHighThresh -= 0.05; 
                        ejLowThresh -= 0.05; 
                    }
                }
                else
                {
                    if (ejHighThresh+0.05 < Config.ejectRateUpperBound)
                    {
                        ejHighThresh += 0.05; 
                        ejLowThresh  += 0.05; 
                    }
                }
#if hillClimb
                Console.WriteLine("High th {0} low th {1}", ejHighThresh, ejLowThresh);
#endif
                Config.ejectRateHighThreshold = ejHighThresh;
                Config.ejectRateLowThreshold  = ejLowThresh;
                if (Config.router.algorithm == RouterAlgorithm.DR_AFC)
                    Simulator.stats.mshrThrottle_afc_total_util.Reset();
                else
                    Simulator.stats.mshrThrottle_smallEpoch_netutil.Reset();
            }

            // Set MSHR water mark (to resize the mshrs) based on the ejection rate
            for (int i = 0; i < Config.N; i++)
            {
                // Default: use the reply rate
                double rate = replyEjectRate[i];

                /* 
                 * Estimate alone ejection rate assuming number of flits in 
                 * flight remains constant.
                 * */
                double factor = (double)Simulator.stats.flit_net_latency_byreqID[i].Avg / Simulator.stats.flit_net_latency_alone_byreqID[i].Avg;
                double estReplyRate = factor * replyEjectCount[i] / Config.ejectMonitorPeriod;
                // Reset for the next period
                Simulator.stats.flit_net_latency_byreqID[i].Reset();
                Simulator.stats.flit_net_latency_alone_byreqID[i].Reset();
#if debugEstimate
                writeNode(i);
                Console.WriteLine("est. alone ejection {0}. Factor-> {1}", estReplyRate, factor);
#endif

                ///////////////////////////////////////////////////////////////////////
                /// Metric used to adjust mshrs watermark
                ///////////////////////////////////////////////////////////////////////
#if debugRate
                writeNode(i);
                Console.WriteLine("-> Injection rate {0}", m_injRate[i]);
                Console.WriteLine("-> Reply Ejection rate {0}", rate);
#endif
                if (Config.useInjRate)
                    rate = m_injRate[i];

                // Use estimated alone ejection rate as a metric for throttling
                if (Config.useEstAloneEjRate)
                {
                    rate = estReplyRate;
                }

                // Use local ejection rate as a metric for throttling (composed of self data reply flits and 
                // other nodes' request flits).
                if (Config.throttleByLocalTotalEject == true)
                    rate = ejectRate[i];
                
                ///////////////////////////////////////////////////////////////////////
                /// Watermark adjustment
                ///////////////////////////////////////////////////////////////////////
                if (Config.bThrottleMSHR == false)
                    continue;

                int watermark = Config.mshrsWatermark[i];
                // Decrease 1 available MSHR
                if (rate >= Config.ejectRateHighThreshold)
                {
                    if (watermark < Config.mshrs)
                        watermark++;
                }
                else if (rate <= Config.ejectRateLowThreshold)
                {
                    if (watermark > 0)
                        watermark--;
                }

                if (watermark > 0)
                {
#if debug
                    writeNode(i);
                    Console.WriteLine("-> Watermark: {0}",watermark);
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
