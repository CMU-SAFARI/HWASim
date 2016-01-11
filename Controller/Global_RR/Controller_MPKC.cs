/**
 *
 * **This is the MICRO version of throttling controller**
 * Three clusters scheme described in the paper that always perform cluster scheduling without any trigger.
 * There's a single netutil target to used as for throttle rate adjustment. The throttle rate is applied to 
 * both high(RR) clusters and the always throttled cluster. The controller can used either filling up total mpki
 * or single app mpki thresh to decide which app to be put into the low intensity cluster. The RR clusters
 * has a threshold value to choose which apps to be selected. If an app's mpki is greater than the threshold,
 * it's put into the always throttled cluster.
 *
 * Always throttled cluster can be disabled, so is total mpki filling for low intensity cluster.
 *
 **/ 

//#define DEBUG_NETUTIL
//#define DEBUG_CLUSTER
//#define DEBUG_CLUSTER2

using System;
using System.Collections.Generic;

namespace ICSimulator
{

    public class Controller_MPKC : Controller_Global_round_robin
    {
        // a pool of clusters for throttle mode
        public static BatchClusterPool cluster_pool;

        // nodes in these clusters are always throttled without given it a free injection slot!
        public static Cluster throttled_cluster;
        public static Cluster low_intensity_cluster;
        public ulong sampling_period = (ulong)Config.throttle_sampling_period;

        string getName(int ID)
        {
            return Simulator.network.workload.getName(ID);
        }
        void writeNode(int ID)
        {
            Console.Write("{0} ({1}) ", ID, getName(ID));
        }

        public Controller_MPKC()
        {
            for (int i = 0; i < Config.N; i++)
            {
                L1misses[i] = 0;
                MPKI[i]     = 0.0;
                MPKC[i]     = 0.0;
                m_isThrottled[i] = false;
                num_ins_last_epoch[i] = 0;
            }

            throttled_cluster     = new Cluster();
            low_intensity_cluster = new Cluster();
            cluster_pool          = new BatchClusterPool(Config.cluster_MPKI_threshold);
        }

        public override void doThrottling()
        {
        }

        public override void doStep()
        {
            //sampling period: Examine the network state and determine whether to throttle or not
            if ((Simulator.CurrentRound % (ulong)sampling_period) == 0)
            {
                setThrottling();
                resetStat();
            }
            //once throttle mode is turned on. Let each cluster run for a certain time interval
            //to ensure fairness. Otherwise it's throttled most of the time.
            if ((Simulator.CurrentRound % (ulong)Config.interval_length) == 0)
                doThrottling();
        }

        public override void resetStat()
        {
            for (int i = 0; i < Config.N; i++)
            {
                num_ins_last_epoch[i] = Simulator.stats.every_insns_persrc[i].Count;
                L1misses[i]=0;
            }            
        }

        public override void setThrottling()
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
                writeNode(i);
                insnsRetired = Simulator.stats.every_insns_persrc[i].Count - num_ins_last_epoch[i];;
                if (insnsRetired < 0)
                    throw new Exception("Error gathering instructions!");

                prev_MPKI[i] = MPKI[i];
                MPKI[i] = (insnsRetired == 0) ? 0 : ((double)(L1misses[i]*1000)) / insnsRetired;
                systemIPC += ((double)insnsRetired) / sampling_period;
                MPKC[i] = (double)(L1misses[i]*1000) / sampling_period;
                Console.WriteLine("MPKI: {0} MPKC: {1}", MPKI[i], MPKC[i]);
                sumMPKC += MPKC[i];
                sumMPKI += MPKI[i];
            }
            recordStats();
            Console.WriteLine("Sum MPKI {0} MPKC {1} Avg MPKC {2}", sumMPKI, sumMPKC, sumMPKC / Config.N);
        }

        public int CompareByMpki(int x,int y)
        {
            if(MPKI[x]-MPKI[y]>0.0) return 1;
            else if(MPKI[x]-MPKI[y]<0.0) return -1;
            return 0;
        }
    }
}
