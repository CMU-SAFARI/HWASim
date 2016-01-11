/**
 * @brief Without worrying about actual implementatin, every sampling period
 * gather MPKC and sends total MPKC average to every node. Each node then
 * adjusts its own throttle rate to reach the avearge.
 **/ 

using System;
using System.Collections.Generic;

namespace ICSimulator
{

    public class Controller_MPKC_Cluster : Controller_ClassicBLESS
    {
        public double[] throttleRate = new double[Config.N];
        IPrioPktPool[] m_injPools = new IPrioPktPool[Config.N];

        // Average MPKC of all the nodes in the network 
        double avgMPKC;


        public static Cluster neverThrottledCluster;

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
        // true to allow injection, false to block (throttle)
        // RouterFlit uses this function to determine whether it can inject or not
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
        public Controller_MPKC_Cluster()
        {
            for (int i = 0; i < Config.N; i++)
            {
                L1misses[i] = 0;
                MPKI[i]     = 0.0;
                throttleRate[i]  = 0.0;
                num_ins_last_epoch[i] = 0;
            }
            avgMPKC = 0.0;
            neverThrottledCluster = new Cluster();
        }

        public override void doStep()
        {
            if ((Simulator.CurrentRound % Config.throttlePeriod) == 0)
            {
                throttle();
                resetStat();
            }
        }
        
        //
        // Gather each node's state: MPKI, MPKC, and total average MPKC. Throttle each node until
        // each node's MPKC is around avg MPKC.
        //
        public void throttle()
        {
            Console.WriteLine("cycle {0} @", Simulator.CurrentRound);
            Console.WriteLine("Netutil {0}", Simulator.network._cycle_netutil);

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
                systemIPC += ((double)insnsRetired) / Config.throttle_sampling_period;
                MPKC[i] = (double)(L1misses[i]*1000) / Config.throttle_sampling_period;
                Console.WriteLine("MPKI: {0} MPKC: {1}", MPKI[i], MPKC[i]);
                sumMPKC += MPKC[i];
                sumMPKI += MPKI[i];
            }
            Console.WriteLine("Sum MPKI {0} MPKC {1} Avg MPKC {2}", sumMPKI, sumMPKC, sumMPKC / Config.N);
            avgMPKC = sumMPKC / Config.N;

            Console.WriteLine("----Throttle Rate----");
            // Set throttle rate based on the average MPKC
            for (int i = 0; i < Config.N; i++)
            {
                // Increase 1% of throttle rate
                if (MPKC[i] > avgMPKC)
                {
                    if (throttleRate[i] < 1.0)
                        throttleRate[i] += 0.01;
                }
                else if (MPKC[i] < avgMPKC && throttleRate[i] >= 0.01)
                    throttleRate[i] -= 0.01;
                Console.WriteLine("{0} :: {1}", i, throttleRate[i]);
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
