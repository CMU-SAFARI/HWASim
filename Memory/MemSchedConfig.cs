using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

namespace ICSimulator
{
    public class MemSchedConfig : ConfigGroup
    {
        public bool is_omniscient = false;    //whether all memory share the same controller
        public string sched_algo = "FCFS";
        public Type sched_algo_type;
        

        //prioritize row-hits
        public bool prioritize_row_hits = false;

        /* //writeback
        public int ignore_wb = 1;                //do not generate writeback requests for evicted cache blocks
        public bool is_wb_sched = false;    //special scheduling consideration for writeback requests
        public double wb_full_ratio = 0.7;       //threshold until non-writeback requests are prioritized
        */

        /*************************
         * FRFCFS Scheduler
         *************************/
        public int row_hit_cap = 4;

        /*************************
         * STFM Scheduler
         *************************/
        public double alpha = 1.1;
        public ulong beta = 1048576;
        public double gamma = 0.5;
        public int ignore_gamma = 0;

        /*************************
         * ATLAS Scheduler
         *************************/
        public int quantum_cycles = 1000000;
        public ulong threshold_cycles = 100000;
        public double history_weight = 0.875;
        public bool service_overlap = false;

        /*************************
         * PAR-BS Scheduler
         *************************/
        public int batch_cap = 5;
        public int prio_max = 11;   //0~9 are real priorities, 10 is no-priority

        //schedulers: FR_FCFS_Cap, NesbitFull
        public ulong prio_inv_thresh = 0;        //FRFCFS_Cap, NesbitFull schedulers; in memory cycles

        //schedulers: STFM, Nesbit{Basic, Full}
        public int use_weights = 0;
        public double[] weights = new double[128];

        /*************************
         * TCM Scheduler
         *************************/
        public double AS_cluster_factor = 0.10;

        //shuffle
        public SchedTCM.ShuffleAlgo shuffle_algo = SchedTCM.ShuffleAlgo.Hanoi;
        public int shuffle_cycles = 800;
        public bool is_adaptive_shuffle = true;
        public double adaptive_threshold = 0.1;  
        /*************************
         * SlackAware Scheduler
         *************************/
        public int instCountDiff = 10000;

	/* HWA CODE */
	public int hwa_row_hit_max = 8;
	public bool is_qos_deadline = false;
	public bool is_slack_deadline = false;
	public double HWA_threshold_factor = 0.50;
	public bool is_hwa_sched_edf = false;
	public bool is_fine_grained_hwa_prior = false;
	public bool is_hwa_threshold_used = true;
	public bool is_used_current_in_insert_hwa = false;
	public int hwa_phase_num = 10;
	public bool hwa_str_priority = false;
	public bool is_hwa_sched_rm = false;
	public bool is_hwa_sched_wkld = false;
	public bool hwa_priority_per_bank = false;
	public double effective_bw_ratio = 0.5;
	public double sharedThreshold = 0.5;
	public int unitProbability = 2;
	public double hwa_emergency_progress = 0.9;
	public double hwa_emergency_progress_short = 0.9;
	public double hwa_emergency_progress_long = 0.9;

	public string QoSPolicy = "BW";
	public ulong qosQuantum = 0;
	public ulong qosEpoch = 0;
	public bool qosWorstCaseStrict = false;
	public bool isQosOtherChExcluded = false;
	public int qosPreDstCheckNum = 0;
	public bool qosReGetInstNumEachDeadline = false;

	public bool is_hwa_cap_high_priority = false;

	public bool is_used_prior_for_sdl_cluster = false;
	public double ratio_allocated_sdl_cluster = 1.0;
	public bool is_sdllclst_used = true;
	public bool is_always_llclst_accelerated = true;
	public int threshold_for_accel_llclst = 1;
	public int phase_pred_entry_num = 256;
	public int workload_pred_entry_num = 256;
	public bool workload_pred_perctrl = false;
	public bool workload_pred_worst = false;
	public bool is_llclst_mmap_block_interleave = false;
	public bool is_all_mmap_block_interleave = false;

	public int black_list_threshold = 4;
	public ulong black_list_quantum = 10000;
	public bool is_clustering_auto_adjust = false;
	public bool is_auto_adjust_alllow = false;
	public int auto_adjust_threshold = 0;
	public int auto_adjust_voting_cnt = 1;
	public int auto_adjust_voting_threshold = 0;
	public bool is_auto_adjust_each_stat = false;

	public double dual_mode_factor = 0.1;
	public int roundrobin_quantum = 2; // set multiplied value by channel num

        public string fixPriorityList = "";

	public double clustering_rbl_threshold = 0.5;
	public int accelerate_probability_nonint = 100;
	public int accelerate_probability_int = 0;
	public int accelerate_probability_int_min = 0;

	public bool is_clustering_and_probability = false;
	public bool is_clustering_and_probability_dual = false;
	public bool is_clustering_th_and_prob = false;
	public bool is_gpu_inaccurate_estimate = false;
	public double gpu_inaccurate_estimate_rate = 1.0;

	public int quantum_cycles_for_probability = 10000;
	public double prob_minimum_hw_bandwidth_rate = 0.9;

	public int quantum_cycles_for_suspend = 1000000;
	public int threshold_bw_shortage = 0; // default is invalid

	public bool hwa_frfcfs_deadline_same_priority = false;
	public bool is_extend_overwrap_sdllclst = true;

	public bool static_prior_gpu_is_lower_cpu = false;

	public int hwaBankCheckNum = 0;
	public bool is_chunk_base = false;
	/* HWA CODE END */

        protected override bool setSpecialParameter(string param, string val)
        {
            return false;
        }

        public override void finalize()
        {
            //memory scheduling algo
            string type_name = Config.sched.sched_algo;
            try{
                sched_algo_type = Type.GetType(type_name);
            }
            catch{
                throw new Exception(String.Format("Scheduler not found {0}", Config.sched.sched_algo));
            }

            /*
            //normalize weights
            if (use_weights != 0)
            {
                MemSchedAlgo algo = Config.sched.mem_sched_algo;
                if (algo == MemorySchedulingAlgorithm.FQM_BASIC || algo == MemorySchedulingAlgorithm.FQM_FULL)
                {
                    double total_weight = 0;
                    foreach (int i in Config.sched.weights)
                        total_weight += i;
                    for (int i = 0; i < Config.N; i++)
                        Config.sched.weights[i] /= total_weight;
                }
            }
            */
        }
    }
}
