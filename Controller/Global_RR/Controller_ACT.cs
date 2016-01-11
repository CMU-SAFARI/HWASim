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

    public class Controller_ACT : Controller_Global_round_robin
    {
        public static double lastSystemIPC;
        // a pool of clusters for throttle mode
        public static BatchClusterPool cluster_pool;

        // nodes in these clusters are always throttled without given it a free injection slot!
        public static Cluster throttled_cluster;
        public static Cluster low_intensity_cluster;

        public static double maxIPC;

        string getName(int ID)
        {
            return Simulator.network.workload.getName(ID);
        }
        void writeNode(int ID)
        {
            Console.Write("{0} ({1}) ", ID, getName(ID));
        }

        public Controller_ACT()
        {
            for (int i = 0; i < Config.N; i++)
            {
                L1misses[i] = 0;
                MPKI[i]     = 0.0;
                m_isThrottled[i] = false;
                num_ins_last_epoch[i] = 0;
            }

            maxIPC        = 0.0;
            lastSystemIPC = 0;

            throttled_cluster     = new Cluster();
            low_intensity_cluster = new Cluster();
            cluster_pool          = new BatchClusterPool(Config.cluster_MPKI_threshold);
        }

        public override void doThrottling()
        {
            //All nodes in the low intensity can alway freely inject.
            int [] low_nodes=low_intensity_cluster.allNodes();

            if(low_nodes.Length>0)
            {
#if DEBUG_CLUSTER2
            Console.WriteLine("\n:: cycle {0} ::", Simulator.CurrentRound);
            Console.Write("\nLow nodes *NOT* throttled: ");
#endif
                foreach (int node in low_nodes)
                {
#if DEBUG_CLUSTER2
                    writeNode(node);
#endif
                    setThrottleRate(node,false);
                    m_nodeStates[node] = NodeState.Low;
                }
            }

            //Throttle all the high other nodes
            int [] high_nodes=cluster_pool.allNodes();
#if DEBUG_CLUSTER2
            Console.Write("\nAll high other nodes: ");
#endif
            foreach (int node in high_nodes)
            {
#if DEBUG_CLUSTER2
                writeNode(node);
#endif
                setThrottleRate(node,true);
                m_nodeStates[node] = NodeState.HighOther;
            }

            //Unthrottle all the nodes in the free-injecting cluster
            int [] nodes=cluster_pool.nodesInNextCluster();
#if DEBUG_CLUSTER2
            Console.Write("\nUnthrottling cluster nodes: ");
#endif
            if(nodes.Length>0)
            {
                foreach (int node in nodes)
                {
                    setThrottleRate(node,false);
                    m_nodeStates[node] = NodeState.HighGolden;
                    Simulator.stats.throttle_time_bysrc[node].Add();
#if DEBUG_CLUSTER2
                    writeNode(node);
#endif
                }
            }

            /* Throttle nodes in always throttled mode. */
            int [] throttled_nodes=throttled_cluster.allNodes();

            if(throttled_nodes.Length>0)
            {
#if DEBUG_CLUSTER2
                Console.Write("\nAlways Throttled nodes with rate {0}: ", Config.RR_throttle_rate);
#endif
                foreach (int node in throttled_nodes)
                {
                    setThrottleRate(node,true);
                    //TODO: need another state for throttled throttled_nodes
                    m_nodeStates[node] = NodeState.AlwaysThrottled;
                    Simulator.stats.always_throttle_time_bysrc[node].Add();
#if DEBUG_CLUSTER2
                    writeNode(node);
#endif
                }
            }

#if DEBUG_CLUSTER2
            Console.Write("\n*NOT* Throttled nodes: ");
            for(int i=0;i<Config.N;i++)
                if(!m_isThrottled[i])
                    writeNode(i);
            Console.Write("\n");
#endif
        }

        /* Need to have a better dostep function along with RR monitor phase.
         * Also need to change setthrottle and dothrottle*/

        public override void doStep()
        {
            //sampling period: Examine the network state and determine whether to throttle or not
            if ((Simulator.CurrentRound % (ulong)Config.throttle_sampling_period) == 0)
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

        //
        // Adjust throttle rate based on a netutil target, which can be static or dynamic.
        // -static target ~ 60%
        // -dynamic target: adjust the target based on system IPC
        //
        void ACTRateAdjust(double systemIPC)
        {
            double netutil = Simulator.network._cycle_netutil;

            if (netutil < 0)
                throw new Exception("incorrect netutil");

            if (Config.bUseBufUtil && Config.router.algorithm == RouterAlgorithm.DR_AFC)
            {
#if DEBUG_NETUTIL
                Console.WriteLine("\nlink util = {0} buf util {1}",
                        netutil,Simulator.network._afc_buffer_util);
#endif
                netutil = Simulator.network._afc_total_netutil;
            }

#if DEBUG_NETUTIL
            Console.WriteLine("\n*****avg netUtil = {0} TARGET at {1}",
                    netutil,Config.netutil_throttling_target);
#endif

            // Adjust util target dynamically based on sys ipc - Simple hill-climb
            /*if (Config.bDynamicUtilTarget)
            {
                if (Config.bMaxIPCTarget)
                {
                    if (systemIPC >= maxIPC)
                    {
                        Config.netutil_throttling_target = netutil;
                        maxIPC = systemIPC;
                    }
                }
                else
                {
                    Config.max_throttle_rate = 1.0; // Don't set any bound on throttle rate.
                    double target = Config.netutil_throttling_target;

                    // Remains
                    if (lastSystemIPC == 0.0)
                        target = netutil;
                    else
                    {
                        if (systemIPC > lastSystemIPC)
                            target -= 0.02;
                        else
                            target += 0.01;
                    }

                    if (target <= 0) target = 0.0; // 0%
                    if (target >= 1) target = 1.0; // 100%

                    lastSystemIPC = systemIPC;
                    Config.netutil_throttling_target = target;
                }
                Simulator.stats.netutilTarget.Add(Config.netutil_throttling_target);
            }*/

            // Un-throttle the network
            for(int i=0;i<Config.N;i++)
                setThrottleRate(i,false);

            // -- Dyanmic throttle rate adjustment
            double th_rate = Config.RR_throttle_rate;
            double adjust_rate = 0;

            if(th_rate>=0 && th_rate<0.7)
                adjust_rate=0.1;//10%
            else if(th_rate<0.90)
                adjust_rate=0.02;//2%
            else
                adjust_rate=0.01;//1%

            if (netutil < Config.netutil_throttling_target)
            {
                if ((th_rate-adjust_rate) >= 0)
                    Config.RR_throttle_rate -= adjust_rate;
                else
                    Config.RR_throttle_rate = 0;
            }
            else
            {
                if ((th_rate+adjust_rate) <= Config.max_throttle_rate)
                    Config.RR_throttle_rate += adjust_rate;
                else
                    Config.RR_throttle_rate = Config.max_throttle_rate;
            }
            if (Config.RR_throttle_rate < 0 || Config.RR_throttle_rate > Config.max_throttle_rate)
                throw new Exception("Throttle rate out of range (0 , max value)!!");
            Simulator.stats.total_th_rate.Add(Config.RR_throttle_rate);
            // -- END throttling rate adjustment

            // Disable throttling
            if (Config.bNoThrottle)
                Config.RR_throttle_rate = 0;
        }

        public override void setThrottling()
        {
#if DEBUG_NETUTIL
            Console.Write("\n:: cycle {0} ::",
                    Simulator.CurrentRound);
            Console.WriteLine("cycle {0} @", Simulator.CurrentRound);
            Console.WriteLine("Netutil {0}", Simulator.network._cycle_netutil);
#endif

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
#if DEBUG_NETUTIL
                Console.WriteLine("MPKI: {0} MPKC: {1}", MPKI[i], MPKC[i]);
#endif
                sumMPKC += MPKC[i];
                sumMPKI += MPKI[i];
            }

            if (Config.bUseMPKC)
            {
                MPKC = MPKI;
                sumMPKI = sumMPKC;
            }

            recordStats();
#if DEBUG_NETUTIL
            Console.WriteLine("Sum MPKI {0} MPKC {1} Avg MPKC {2}", sumMPKI, sumMPKC, sumMPKC / Config.N);
#endif

            ACTRateAdjust(systemIPC);

#if DEBUG_NETUTIL
            Console.WriteLine("*****Adjusted throttle rate: {0}",Config.RR_throttle_rate);
#endif
            // Clear the cluster formatoin from the last epoch
            cluster_pool.removeAllClusters();
            throttled_cluster.removeAllNodes();
            low_intensity_cluster.removeAllNodes();

            List<int> sortedList = new List<int>();
            double total_mpki=0.0;
            double small_mpki=0.0;
            double current_allow=0.0;
            int total_high=0;

            for(int i=0;i<Config.N;i++)
            {
                sortedList.Add(i);
                total_mpki+=MPKI[i];
                //stats recording-see what's the total mpki composed by low/med apps
                if(MPKI[i]<=30)
                    small_mpki+=MPKI[i];
            }

            //sort by mpki
            if (Config.act_reverse_sort)
                sortedList.Sort(ReverseCompareByMpki);
            else
                sortedList.Sort(CompareByMpki);

            //foreach (int sortListIdx in sortedList)
            //    Console.WriteLine("Idx: {0} MPKI: {1}", sortListIdx, MPKI[sortListIdx]);

#if DEBUG_CLUSTER
            for(int i=0;i<Config.N;i++)
            {
                writeNode(sortedList[i]);
                Console.WriteLine("-->MPKI:{0}",MPKI[sortedList[i]]);
            }
            Console.WriteLine("*****total MPKI: {0}",total_mpki);
            Console.WriteLine("*****total MPKIs of apps with MPKI<30: {0}\n",small_mpki);
#endif
            //find the first few apps that will be allowed to run freely without being throttled
            for(int list_index=0;list_index<Config.N;list_index++)
            {
                int node_id=sortedList[list_index];
#if DEBUG_CLUSTER
                writeNode(node_id);
#endif

                /*
                 * Low intensity cluster conditions:
                 * 1. filling enabled, then fill the low cluster up til free_total_mpki.
                 * 2. if filling not enabled, then apps with mpki lower than 'low_apps_mpki_thresh' will be put into the cluster.
                 * */

                // Need to do this for the GPU
                if(((Config.free_total_MPKI>0 && (current_allow+MPKI[node_id]<=Config.free_total_MPKI) && Config.low_cluster_filling_enabled) ||
                    (!Config.low_cluster_filling_enabled && MPKI[node_id]<=Config.low_apps_mpki_thresh) ) && (node_id!=Config.gpuLoc))
                {
#if DEBUG_CLUSTER
                    Console.WriteLine("->Low node: {0}",node_id);
#endif
                    low_intensity_cluster.addNode(node_id,MPKI[node_id]);
                    current_allow+=MPKI[node_id];
                    Simulator.stats.low_cluster[node_id].Add();
                    continue;
                }
                else if(node_id == Config.gpuLoc || (MPKI[node_id] >= Config.cluster_MPKI_threshold && Config.always_cluster_enabled))
                {
                    //If an application doesn't fit into one cluster, it will always be throttled
#if DEBUG_CLUSTER
                    Console.WriteLine("->Alway throttled node: {0}",node_id);
#endif
                    throttled_cluster.addNode(node_id,MPKI[node_id]);
                    Simulator.stats.high_cluster[node_id].Add();
                    total_high++;
                }
                else
                {
#if DEBUG_CLUSTER
                    Console.WriteLine("->RR node: {0}",node_id);
#endif
                    cluster_pool.addNewNode(node_id,MPKI[node_id]);
                    Simulator.stats.rr_cluster[node_id].Add();
                    total_high++;
                }
            }

            //randomly start a cluster to begin with instead of always the first one
            cluster_pool.randClusterId();
#if DEBUG_CLUSTER
            Console.WriteLine("total high: {0}",total_high);
            Console.WriteLine("-->low cluster mpki: {0}",current_allow);
#endif
            //STATS
            Simulator.stats.allowed_sum_mpki.Add(current_allow);
            Simulator.stats.total_sum_mpki.Add(total_mpki);

            sortedList.Clear();
#if DEBUG_CLUSTER
            cluster_pool.printClusterPool();
#endif
        }

        public int CompareByMpki(int x,int y)
        {
            if(MPKI[x]-MPKI[y]>0.0) return 1;
            else if(MPKI[x]-MPKI[y]<0.0) return -1;
            return 0;
        }

        public int ReverseCompareByMpki(int x,int y)
        {
            if(MPKI[x]-MPKI[y]>0.0) return -1;
            else if(MPKI[x]-MPKI[y]<0.0) return 1;
            return 0;
        }
    }

    //TODO;
    public class HighAloneClusterPool: BatchClusterPool
    {
        public static double high_mpki_thresh=50;
        public HighAloneClusterPool(double mpki_threshold)
        :base(mpki_threshold)
        {
            _mpki_threshold=mpki_threshold;
            q=new List<Cluster>();
            nodes_pool=new List<int>();
            _cluster_id=0;
        }

        public void addNewNodeUniform(int id, double mpki)
        {
            //TODO: add nodes to clusters. However, prevent
            //app with mpki higher than high_mpki_thresh
            //get into a cluster with ohter med/low apps.
        }
    }
}
