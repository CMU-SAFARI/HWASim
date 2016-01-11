//#define DEBUG
//#define THROTTLE_ALL

using System;
using System.Collections.Generic;

namespace ICSimulator
{
    public class Controller_SelfTuned_Buffer : Controller_ClassicBLESS
    {
        IPrioPktPool[] m_injPools = new IPrioPktPool[Config.N];
        AveragingWindow avg_netutil, avg_pkt;
        double m_lastPkt, m_target, m_rate;
        double max_pkt, max_target, max_netu;
        int maxResetCount;

        public Controller_SelfTuned_Buffer()
        {
            avg_netutil = new AveragingWindow(Config.buffer_selftuned_quantum);
            avg_pkt = new AveragingWindow(Config.buffer_selftuned_hillClimb_quantum);

            m_lastPkt = 0.0;
            m_target = 0.0;
            m_rate = 0.0;
            max_pkt = 0.0; 
            max_target = 0.0;
            max_netu = 0.0;
            maxResetCount = 0;
        }

        public override IPrioPktPool newPrioPktPool(int node)
        {
#if THROTTLE_ALL
            return new MultiQPrioPktPool();
#else
            return MultiQThrottlePktPool.construct();
#endif
        }

        public override void setInjPool(int node, IPrioPktPool pool)
        {
            m_injPools[node] = pool;
            pool.setNodeId(node);
        }

        public override bool ThrottleAtRouter
        { get
            {
#if THROTTLE_ALL
                return true;
#else
                return false;
#endif
            }
        }

        // true to allow injection, false to block (throttle)
        public override bool tryInject(int node)
        {
            if (m_rate > 0.0)
                return Simulator.rand.NextDouble() > m_rate;
            else
                return true;
        }

        public override void doStep()
        {
            //avg_netutil.accumulate(Simulator.network._cycle_netutil);
            avg_netutil.accumulate(Simulator.network._fullBufferPercentage);
            // BW performance for hill climbing
            avg_pkt.accumulate(Simulator.network._ejectPacketCount);
            //Console.WriteLine("total eject packet count {0}", Simulator.network._ejectPacketCount);
            Simulator.stats.selftuned_buf_netu.Add(Simulator.network._fullBufferPercentage);

            if (Simulator.CurrentRound % (ulong)Config.buffer_selftuned_quantum == 0)
                doUpdate();

            if (Simulator.CurrentRound % (ulong)Config.buffer_selftuned_hillClimb_quantum == 0)
                GoClimbAMountain();
        }

        void doUpdate()
        {
            // Use full buffer percentage as the metric to throttle
            double  netu = Simulator.network._fullBufferPercentage;
            Simulator.stats.selftuned_buf_netu_coarse.Add(netu);
#if DEBUG
            Console.WriteLine("Netu {0} target {1}", netu, m_target); 
#endif
            m_rate = (netu > m_target) ? 1.00 : 0.00;

            if (m_rate == 1.0)
                Simulator.stats.selftuned_buf_throttle_count.Add();
        }

        void GoClimbAMountain()
        {
            double pkt = avg_pkt.average();
            double last_pkt = m_lastPkt;
            m_lastPkt = pkt;

            // Too many max reset count
            if (maxResetCount > Config.buffer_selftuned_reset_count)
            {
                max_pkt = 0.0;
                maxResetCount = 0;
            }

            // Keep track of max point
            if (pkt > max_pkt)
            {
                max_target = m_target;
                max_netu = avg_netutil.average();
                max_pkt = pkt;
            }
            else
            {
                // Drop from the maximum recorded threshold. Reset max target
                if (max_pkt > 0 && pkt / max_pkt < Config.buffer_selftuned_max_drop)
                {
                    if (max_netu > max_target)
                        m_target = max_target;
                    else
                        m_target = max_netu;

                    maxResetCount++;
                    // already adjust the target
                    return;
                }
            }
            maxResetCount = 0;
#if DEBUG
            Console.WriteLine("cycle {0}: last pkt {1}, cur pkt {2}, cur target {3}",
                    Simulator.CurrentRound, last_pkt, pkt, m_target);
#endif

            // Self-Tuned Congestion Networks, Table 1 (p. 6)
            if (last_pkt > 0 && pkt / last_pkt < Config.buffer_selftuned_drop_threshold) // drop > 25% from last period?
            {
                if ((m_target - Config.buffer_selftuned_target_decrease) > 0.0)
                    m_target -= Config.buffer_selftuned_target_decrease;
#if DEBUG
                Console.WriteLine("--> decrease to {0}", (int) m_target);
#endif
            }
            else
            {
                if (m_rate > 0.0) // increase when it's throttling
                {
                    // no significant drop. increase if target is < 1.0.
                    if ((m_target + Config.buffer_selftuned_target_increase) < 1.0)
                        m_target += Config.buffer_selftuned_target_increase;
                }
#if DEBUG
                Console.WriteLine("--> increase to {0}", m_target);
#endif
            }

        }
    }
}
