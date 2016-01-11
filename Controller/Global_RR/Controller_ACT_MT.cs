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

using System;
using System.Collections.Generic;

namespace ICSimulator
{

    public class Controller_ACT_MT : Controller_Global_Batch
    {

        //nodes in these clusters are always throttled without given it a free injection slot!
        public static Cluster throttled_cluster;
        public static Cluster low_intensity_cluster;

        public Workload wkld { get { return Simulator.network.workload; } }

        public Controller_ACT_MT()
        {
            for (int i = 0; i < Config.N; i++)
            {
                MPKI[i]=0.0;
                num_ins_last_epoch[i]=0;
                m_isThrottled[i]=false;
                L1misses[i]=0;
                lastNetUtil = 0;
            }
            throttled_cluster=new Cluster();
            low_intensity_cluster=new Cluster();
            cluster_pool=new BatchClusterPool(Config.cluster_MPKI_threshold);
        }

        public override void doThrottling()
        {
            //All nodes in the low intensity can alway freely inject.
            int [] low_grps=low_intensity_cluster.allNodes();
            foreach (int grp in low_grps)
            {
                for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                {
                    int node = wkld.mapThd(grp, thd);
                    m_nodeStates[node] = NodeState.Low;
                    setThrottleRate(node,false);
                }
            }

            //Throttle all the high other nodes
            int [] high_grps=cluster_pool.allNodes();
            foreach (int grp in high_grps)
            {
                for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                {
                    int node = wkld.mapThd(grp, thd);
                    setThrottleRate(node,true);
                    m_nodeStates[node] = NodeState.HighOther;
                }
            }

            //Unthrottle all the nodes in the free-injecting cluster
            int [] grps=cluster_pool.nodesInNextCluster();
            foreach (int grp in grps)
            {
                for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                {
                    int node = wkld.mapThd(grp, thd);
                    setThrottleRate(node,false);
                    m_nodeStates[node] = NodeState.HighGolden;
                    Simulator.stats.throttle_time_bysrc[node].Add();
                }
            }

            /* Throttle nodes in always throttled mode. */
            int [] throttled_grps=throttled_cluster.allNodes();
            
            foreach (int grp in throttled_grps)
            {
                for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                {
                    int node = wkld.mapThd(grp, thd);
                    setThrottleRate(node,true);
                    m_nodeStates[node] = NodeState.AlwaysThrottled;
                    Simulator.stats.always_throttle_time_bysrc[node].Add();
                }
            }
        }

        /* Need to have a better dostep function along with RR monitor phase.
         * Also need to change setthrottle and dothrottle*/
 
        public override void doStep()
        {
            //sampling period: Examine the network state and determine whether to throttle or not
            if ( (Simulator.CurrentRound % (ulong)Config.throttle_sampling_period) == 0)
            {
                setThrottling();
                lastNetUtil = Simulator.stats.netutil.Total;
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
            //get the MPKI values
            for (int grp = 0; grp < wkld.GroupCount; grp++)
            {
                ulong insns = 0, misses = 0;
                for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                {
                    int node = wkld.mapThd(grp, thd);
                    insns += Simulator.stats.every_insns_persrc[node].Count - num_ins_last_epoch[node];
                    misses += L1misses[node];
                }
                if (insns == 0)
                    MPKI[grp] = 0.0;
                else
                    MPKI[grp] = (double)misses / insns * 1000.0;
            }       

            recordStats();

            double netutil=((double)(Simulator.stats.netutil.Total-lastNetUtil)/(double)Config.throttle_sampling_period);
            /* 
             * 1.If the netutil remains high, lower the threshold value for each cluster
             * to reduce the netutil further more and create a new pool/schedule for 
             * all the clusters. How to raise it back?
             * Worst case: 1 app per cluster.
             *
             * 2.Find the difference b/w the current netutil and the threshold.
             * Then increase the throttle rate for each cluster based on that difference.
             *
             * 3.maybe add stalling clusters?
             * */
            //un-throttle the network
            for(int i=0;i<Config.N;i++)
                setThrottleRate(i,false);

            double th_rate=Config.RR_throttle_rate;
            double adjust_rate=0;
            if(th_rate>=0 && th_rate<0.7)
                adjust_rate=0.1;//10%
            else if(th_rate<0.90)
                adjust_rate=0.02;//2%
            else
                adjust_rate=0.01;//1%


            if(netutil<Config.netutil_throttling_target)
            {
                if((th_rate-adjust_rate)>=0)
                    Config.RR_throttle_rate-=adjust_rate;
                else
                    Config.RR_throttle_rate=0;
            }
            else
            {
                if((th_rate+adjust_rate)<=Config.max_throttle_rate)
                    Config.RR_throttle_rate+=adjust_rate;
                else
                    Config.RR_throttle_rate=Config.max_throttle_rate;
            }
            if(Config.RR_throttle_rate<0 || Config.RR_throttle_rate>Config.max_throttle_rate)
                throw new Exception("Throttle rate out of range (0 , max value)!!");

            Simulator.stats.total_th_rate.Add(Config.RR_throttle_rate);


            cluster_pool.removeAllClusters();
            throttled_cluster.removeAllNodes();
            low_intensity_cluster.removeAllNodes();

            List<int> sortedList = new List<int>();
            double total_mpki=0.0;
            double small_mpki=0.0;
            double current_allow=0.0;
            int total_high=0;

            for (int grp = 0; grp < wkld.GroupCount; grp++)
            {
                sortedList.Add(grp);
                total_mpki+=MPKI[grp];
                //stats recording-see what's the total mpki composed by low/med apps
                if(MPKI[grp]<=30)
                    small_mpki+=MPKI[grp];
            }
            //sort by mpki
            sortedList.Sort(CompareByMpki);

            //find the first few apps that will be allowed to run freely without being throttled
            for (int grp = 0; grp < wkld.GroupCount; grp++)
            {
                /*
                 * Low intensity cluster conditions:
                 * 1. filling enabled, then fill the low cluster up til free_total_mpki.
                 * 2. if filling not enabled, then apps with mpki lower than 'low_apps_mpki_thresh' will be put into the cluster.
                 * */

                if(((current_allow+MPKI[grp]<=Config.free_total_MPKI) && Config.low_cluster_filling_enabled) ||
                    (!Config.low_cluster_filling_enabled && MPKI[grp]<=Config.low_apps_mpki_thresh))
                {
                    low_intensity_cluster.addNode(grp,MPKI[grp]);
                    current_allow+=MPKI[grp];

                    for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                        Simulator.stats.low_cluster[wkld.mapThd(grp, thd)].Add();

                    continue;
                }
                else if(MPKI[grp]>Config.cluster_MPKI_threshold && Config.always_cluster_enabled)
                {
                    //If an application doesn't fit into one cluster, it will always be throttled
                    throttled_cluster.addNode(grp, MPKI[grp]);

                    for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                        Simulator.stats.high_cluster[wkld.mapThd(grp, thd)].Add();

                    total_high++;
                }
                else
                {
                    cluster_pool.addNewNode(grp, MPKI[grp]);

                    for (int thd = 0; thd < wkld.getGroupSize(grp); thd++)
                        Simulator.stats.rr_cluster[wkld.mapThd(grp, thd)].Add();

                    total_high++;
                }
            } 
            //randomly start a cluster to begin with instead of always the first one
            cluster_pool.randClusterId();
            //STATS
            Simulator.stats.allowed_sum_mpki.Add(current_allow);
            Simulator.stats.total_sum_mpki.Add(total_mpki);

            sortedList.Clear();
        }

        public int CompareByMpki(int x,int y)
        {
            if(MPKI[x]-MPKI[y]>0.0) return 1;
            else if(MPKI[x]-MPKI[y]<0.0) return -1;
            return 0;
        }
    }
}
