using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedTCM : Scheduler
    {
        //rank
        public int[] rank;

        //attained service
        public double[] service;
        public double[] curr_service;
        public uint[] service_bank_cnt;

        //mpki
        public double[] mpki;
        public ulong[] prev_cache_miss;
        public ulong[] prev_inst_cnt;

        //rbl
        public double[] rbl;
        public ulong[] shadow_row_hits;
        public double rbl_diff;

        //blp
        public double[] blp;
        public uint[] blp_sample_sum;
        public uint blp_sample_cnt;
        public double blp_diff;

        //quantum
        public int quantum_cnt;
        public int quantum_cycles_left;

        //shuffle
        public int[] nice;
        public int shuffle_cnt;
        public int shuffle_cycles_left;

        //shuffle
        public Random rand = new Random(0);
        public enum ShuffleAlgo
        {
            Naive,
            Random,
            Hanoi,
            ControlledRandom
        }

        //cluster sizes
        public int icluster_size;

        public SchedTCM(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
            rank = new int[Config.Ng];

            service = new double[Config.Ng];
            curr_service = new double[Config.Ng];
            service_bank_cnt = new uint[Config.Ng];

            mpki = new double[Config.Ng];
            prev_cache_miss = new ulong[Config.Ng];
            prev_inst_cnt = new ulong[Config.Ng];

            rbl = new double[Config.Ng];
            shadow_row_hits = new ulong[Config.Ng];

            blp = new double[Config.Ng];
            blp_sample_sum = new uint[Config.Ng];

            quantum_cycles_left = Config.sched.quantum_cycles;

            nice = new int[Config.Ng];
            shuffle_cycles_left = Config.sched.shuffle_cycles;
            this.chan = chan;
        }

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
                winner = candidate;
            MemoryRequest req1 = winner.mreq;
            MemoryRequest req2 = candidate.mreq;
            int rank1 = rank[req1.request.requesterID];
            int rank2 = rank[req2.request.requesterID];
            if (rank1 != rank2) {
                if (rank1 > rank2) return winner;
                else return candidate;
            }

            bool hit1 = winner.IsRowBufferHit;
            bool hit2 = candidate.IsRowBufferHit;
            if (hit1 ^ hit2) {
                if (hit1) return winner;
                else return candidate;
            }

            if (candidate.IsOlderThan(winner)) return candidate;
            else return winner;
        }
        
        public override void Tick()
        {
            base.Tick();

            //service
            increment_service();

            //blp
            if (Simulator.CurrentRound % 1000 == 0) {
                sample_blp();
            }

            //shuffle
            if (shuffle_cycles_left > 0) {
                shuffle_cycles_left--;
            }
            else if (quantum_cnt != 0 && icluster_size > 1) {
                shuffle();
                shuffle_cnt++;
                shuffle_cycles_left = Config.sched.shuffle_cycles;
            }

	    /*
	    for (int p = 0; p < Config.Ng; p++)
            {
		if( icluster_size > 0 )
		{
		    if(rank[p]>=icluster_size)
		    {
			Console.WriteLine("Shuffle Rank pid:{0}, Rank:{1} LowCluster", p,rank[p]);
		    }
		    else
		    {
			Console.WriteLine("Shuffle Rank pid:{0}, Rank:{1} HighCluster", p,rank[p]);
		    }
		}
	    }
	    */
            //quantum
            if (quantum_cycles_left > 0) {
                quantum_cycles_left--;
                return;
            }

            //new quantum
            decay_stats();

            quantum_cnt++;
            quantum_cycles_left = Config.sched.quantum_cycles;

            shuffle_cnt = 0;
            shuffle_cycles_left = Config.sched.shuffle_cycles;

            //cluster
            icluster_size = cluster();
            if (icluster_size > 1) assign_nice_rank();

            for (int p = 0; p < Config.Ng; p++)
            {
                if(rank[p]>=icluster_size)
		{
                    Simulator.stats.TimeInLowCluster[p].Add();
//		    Console.WriteLine("Final Rank pid:{0}, Rank:{1} LowCluster", p,rank[p]);
		}
                else
		{
                    Simulator.stats.TimeInHighCluster[p].Add();
//		    Console.WriteLine("Final Rank pid:{0}, Rank:{1} HighCluster", p,rank[p]);
		}
            }
	    /*	    
	    Console.Write("LowCluster");
            for (int p = 0; p < Config.Ng; p++)
		Console.Write("{0},",Simulator.stats.TimeInLowCluster[p].Count);
	    Console.Write("\nHighCluster");
            for (int p = 0; p < Config.Ng; p++)
		Console.Write("{0},",Simulator.stats.TimeInHighCluster[p].Count);
	    Console.Write("\n");
	    */

        }

        public void increment_service()
        {
            for (int p = 0; p < Config.Ng; p++)
                service_bank_cnt[p] = 0;
            MemoryRequest curr_req; 
            //count banks
            for (int i=0;i<chan.maxRequests;i++) {
                if (chan.buf[i].Busy) 
                    curr_req = chan.buf[i].mreq;
                else 
                    continue;
                service_bank_cnt[curr_req.request.requesterID]++;
            }

            //update service
            for (int p = 0; p < Config.Ng; p++) {
                if (!Config.sched.service_overlap) {
                    curr_service[p] += service_bank_cnt[p];
                }
                else {
                    if (service_bank_cnt[p] > 0)
                        curr_service[p] += 1;
                }
            }
        }

        public void sample_blp()
        {
            blp_sample_cnt++;
            for (uint p = 0; p < Config.Ng; p++) {
                uint curr_blp = 0;
                for (int i=0;i<chan.maxRequests;i++) {
                    if ((chan.buf[i].Busy) && (p==chan.buf[i].mreq.request.requesterID)) {
                        curr_blp++;
                    }
                }
                blp_sample_sum[p] += curr_blp;
            }
        }

        virtual public void decay_stats()
        {
            for (int p = 0; p < Config.Ng; p++) {
                ulong cache_miss = Simulator.stats.L2_misses_persrc[p].Count;
                if(p==(Config.Ng-1))
                    cache_miss = Simulator.stats.dramreqs_persrc[p].Count;
                ulong delta_cache_miss = cache_miss - prev_cache_miss[p];
                prev_cache_miss[p] = cache_miss;

                ulong inst_cnt = Simulator.stats.insns_persrc[p].Count; // When warmup cycle is defined, mpki becomes infinity
                ulong delta_inst_cnt = inst_cnt - prev_inst_cnt[p];
                prev_inst_cnt[p] = inst_cnt;

                //mpki
                double curr_mpki = 1000 * ((double)delta_cache_miss) / delta_inst_cnt;
                //GPU
                mpki[p] = Config.sched.history_weight * mpki[p] + (1 - Config.sched.history_weight) * curr_mpki;

                //rbl
		/* HWA CODE Comment Out */
		/*
                double curr_rbl = ((double)shadow_row_hits[p]) / delta_cache_miss;
                rbl[p] = Config.sched.history_weight * rbl[p] + (1 - Config.sched.history_weight) * curr_rbl;
                shadow_row_hits[p] = 0;
		*/
		/* HWA CODE Comment Out End */
		/* HWA CODE */
		//bug of original code??
		ulong row_buffer_hits = Simulator.stats.dramreqs_persrc[p].Count - Simulator.stats.DRAMActivationsPerSrc[p].Count;
		ulong delta_row_buffer_hits = row_buffer_hits - shadow_row_hits[p];

                double curr_rbl = ((double)delta_row_buffer_hits) / delta_cache_miss;
                rbl[p] = Config.sched.history_weight * rbl[p] + (1 - Config.sched.history_weight) * curr_rbl;
                shadow_row_hits[p] = row_buffer_hits;
		/* HWA CODE END */

                //blp
                double curr_blp = ((double)blp_sample_sum[p]) / blp_sample_cnt;
                blp[p] = Config.sched.history_weight * blp[p] + (1 - Config.sched.history_weight) * curr_blp;
                blp_sample_sum[p] = 0;

                //service
                service[p] = curr_service[p];
                curr_service[p] = 0;
            }
            blp_sample_cnt = 0;
        }

        public int cluster()
        {
            //rank
            int[] tids = new int[Config.Ng];
            for (int p = 0; p < Config.Ng; p++)
                tids[p] = p;

            Array.Sort(tids, sort_mpki);
            for (int p = 0; p < Config.Ng; p++) {
                rank[p] = Array.IndexOf(tids, p);
//		Console.WriteLine("pid:{0}, Rank:{1}, mpki:{2}",p,rank[p],mpki[p]);
            }

            //cluster
            int ncluster_size = 0;
            double service_total = 0;
            double service_runsum = 0;

            for (int p = 0; p < Config.Ng; p++)
	    {
//		Console.WriteLine("Service pid:{0}={1}", p, service[p]);
                service_total += service[p];
	    }

            for (int r = Config.Ng - 1; r >= 0; r--) {
                int pid = Array.IndexOf(rank, r);
                service_runsum += service[pid];
//		Console.WriteLine("Check threshold rank:{0}, pid:{1}, service_sum:{2}, threshold:{3}", r, pid, service_runsum, Config.sched.AS_cluster_factor * service_total );
                if (service_runsum > Config.sched.AS_cluster_factor * service_total)
                    break;

                ncluster_size++;
            }

            return Config.Ng - ncluster_size;
        }

        public void shuffle()
        {
            ShuffleAlgo shuffle_algo = Config.sched.shuffle_algo;
            if (Config.sched.is_adaptive_shuffle) {

                double blp_thresh = Config.sched.adaptive_threshold * Config.memory.numBanks;
                double rbl_thresh = Config.sched.adaptive_threshold;
                if (blp_diff > blp_thresh && rbl_diff > rbl_thresh) {
                    shuffle_algo = ShuffleAlgo.Hanoi;
                }
                else {
                    shuffle_algo = ShuffleAlgo.ControlledRandom;
                }
            }

            //rank_to_pid translation
            int[] pids = new int[Config.Ng];
            for (int p = 0; p < Config.Ng; p++) {
                int r = rank[p];
                pids[r] = p;
            }

            //shuffle proper
            switch (shuffle_algo) {
                case ShuffleAlgo.Naive:
                    for (int r = 0; r < icluster_size; r++) {
                        int pid = pids[r];
                        rank[pid] = (r + (icluster_size - 1)) % icluster_size;
                    }
                    break;

                case ShuffleAlgo.ControlledRandom:
                    int step = icluster_size / 2 + 1;
                    for (int r = 0; r < icluster_size; r++) {
                        int pid = pids[r];
                        rank[pid] = (r + step) % icluster_size;
                    }
                    break;

                case ShuffleAlgo.Random:
                    for (int r = icluster_size - 1; r > 0; r--) {
                        int pid1 = Array.IndexOf(rank, r);

                        int chosen_r = rand.Next(r + 1);
                        int chosen_pid = Array.IndexOf(rank, chosen_r);

                        rank[pid1] = chosen_r;
                        rank[chosen_pid] = r;
                    }
                    break;

                case ShuffleAlgo.Hanoi:
                    int even = 2 * icluster_size;
                    int phase = shuffle_cnt % even;

                    if (phase < icluster_size) {
                        int grab_rank = (icluster_size - 1) - phase;
                        int grab_pid = Array.IndexOf(rank, grab_rank);
                        rank[grab_pid] = -1;

                        for (int r = grab_rank + 1; r <= icluster_size - 1; r++) {
                            int pid = Array.IndexOf(rank, r);
                            rank[pid] = r - 1;
                        }
                        rank[grab_pid] = icluster_size - 1;
                    }
                    else {
                        int grab_rank = (icluster_size - 1);
                        int grab_pid = Array.IndexOf(rank, grab_rank);
                        rank[grab_pid] = -1;

                        for (int r = grab_rank - 1; r >= (phase - 1) % icluster_size; r--) {
                            int pid = Array.IndexOf(rank, r);
                            rank[pid] = r + 1;
                        }
                        rank[grab_pid] = (phase - 1) % icluster_size;
                    }
                    break;
            }

            //sanity check
            for (int r = 0; r < Config.Ng; r++) {
                int pid = Array.IndexOf(rank, r);
                Debug.Assert(pid != -1);
            }
        }
        
        public double findRange(double[] arr)
        {
            double min = 99999999.9;
            double max = -1.0;
            for(int i=0;i<arr.Length;i++)
            {
                if(arr[i]>max) max = arr[i];
                if(arr[i]<min) min = arr[i];
            }
            return max-min;
        }
        
        public void assign_nice_rank()
        {
            int[] icluster_pids = new int[icluster_size];
            for (int r = 0; r < icluster_pids.Length; r++) {
                icluster_pids[r] = Array.IndexOf(rank, r);
            }

            int[] pids = new int[icluster_size];

            //blp rank
            Array.Copy(icluster_pids, pids, icluster_size);
            int[] blp_rank = new int[Config.Ng];
            Array.Sort(pids, sort_blp);
            for (int r = 0; r < pids.Length; r++) {
                int pid = pids[r];
                blp_rank[pid] = r;
            }
            blp_diff = findRange(blp);

            //rbl rank
            Array.Copy(icluster_pids, pids, icluster_size);
            int[] rbl_rank = new int[Config.Ng];
            Array.Sort(pids, sort_rbl);
            for (int r = 0; r < pids.Length; r++) {
                int pid = pids[r];
                rbl_rank[pid] = r;
            }
            rbl_diff = findRange(rbl);

            //nice
            Array.Clear(nice, 0, nice.Length);
            for (int r = 0; r < icluster_pids.Length; r++) {
                int pid = icluster_pids[r];
                nice[pid] = blp_rank[pid] - rbl_rank[pid];
            }

            //nice rank
            Array.Copy(icluster_pids, pids, icluster_size);
            int[] nice_rank = new int[Config.Ng];
            Array.Sort(pids, sort_nice);
            for (int r = 0; r < pids.Length; r++) {
                int pid = pids[r];
                nice_rank[pid] = r;
            }

            //copy
            foreach (int pid in icluster_pids) {
                rank[pid] = nice_rank[pid];
            }

            //sanity check
            for (int r = 0; r < Config.Ng; r++) {
                int pid = Array.IndexOf(rank, r);
                Debug.Assert(pid != -1);
            }
        }

        public int sort_mpki(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
            if (pid1 == pid2) return 0;
            
            double mpki1 = mpki[pid1];
            double mpki2 = mpki[pid2];

            if (mpki1 < mpki2) return 1;
            else return -1;
        }

        public int sort_rbl(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
            if (pid1 == pid2) return 0;

            double rbl1 = rbl[pid1];
            double rbl2 = rbl[pid2];

            if (rbl1 < rbl2) return 1;
            else return -1;
        }

        public int sort_blp(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
            if (pid1 == pid2) return 0;

            double blp1 = blp[pid1];
            double blp2 = blp[pid2];

            if (blp1 > blp2) return 1;
            else return -1;
        }

        public int sort_nice(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
            int nice1 = nice[pid1];
            int nice2 = nice[pid2];

            if (nice1 != nice2) {
                if (nice1 > nice2) return 1;
                else return -1;
            }
            return 0;
        }

    }
}
