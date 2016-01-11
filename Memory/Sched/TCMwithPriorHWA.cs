using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedTCMwithPriorHWA : Scheduler
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

	public int log_cnt;
	public int[] req_num;
	public int[] buf_num;

	public int[] hwa_prior;

	public int[] memreq_cnt;
	public ulong next_adjust_time;

        public SchedTCMwithPriorHWA(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
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
	    
	    log_cnt = 0;
	    req_num = new int[Config.Ng];
	    buf_num = new int[Config.Ng];

	    hwa_prior = new int[Config.HWANum];

	    memreq_cnt = new int[Config.Ng];
	    for( int i = 0; i < Config.Ng; i++ )
		memreq_cnt[i] = 0;
        }

	private double deadlinePriority( SchedBuf tgt )
	{
	    if( !Simulator.QoSCtrl.is_HWA(tgt.mreq.request.requesterID) )
//	    if( !Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.is_HWA() )
		{
		    return 1;
		}
	    if(( Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineReq == 0 ) ||
	       ( Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLine == 0 )){
		return 1;

	    }
	    double progress = (double)Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineReqCnt / 
		(double)Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineReq;
	    double target_progress = (double)( Simulator.CurrentRound - Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineCnt ) / 
		(double)Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLine;
	    /*	    Console.WriteLine("ID:{0},{1:x}",tgt.mreq.request.requesterID, tgt.mreq.request.address );
	    Console.WriteLine("{0},{1},{2},{3}",
			      Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineReqCnt,
			      Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineReq,
			      ( Simulator.CurrentRound - Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineCnt ),
			      Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLine ); */
			      		      
	    if( progress > target_progress )
		{
//		Console.WriteLine("HWA:0 progress {0}, target_progress {1}", progress, target_progress );
		return 0;
		}
	    else if( target_progress > 0.9 )
		{
//		    Console.WriteLine("HWA:2 progress {0}, target_progress {1}", progress, target_progress );
		    return 2;
		}
	    else
		{
//		    Console.WriteLine("HWA:1 progress {0:f}, target_progress {1:f}", progress, target_progress );		    
		    return 1;
		}
	}

	private long remainingTime( SchedBuf tgt )
	{
	    long time;
	    
	    time = (long)Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLine + 
		(long)Simulator.network.nodes[tgt.mreq.request.requesterID].cpu.deadLineCnt -
		(long)Simulator.CurrentRound;
	    return(time);
	}
	private long remainingTime( int id )
	{
	    long time;
	    
	    time = (long)Simulator.network.nodes[id].cpu.deadLine + 
		(long)Simulator.network.nodes[id].cpu.deadLineCnt -
		(long)Simulator.CurrentRound;
	    return(time);
	}

        // Override this for other algorithms
	override public void calculate_priority()
	{
	    int hwa_cnt = 0;

	    if( !Config.sched.is_hwa_sched_wkld ) // Based Workload Progress
	    {
		for( int i = 0; i < Config.Ng; i++ )
		{
		    if( Simulator.QoSCtrl.is_HWA(i) )
//		    if( Simulator.network.nodes[i].cpu.is_HWA() )
		    {
			hwa_prior[hwa_cnt++] = i;
		    }
		}
		Array.Sort(hwa_prior, cmp_hwa_priority);
	    }
	    else
	    {
		if(( Simulator.CurrentRound < next_adjust_time ) && ( Config.sched.qosEpoch > 0 ))
		    return;
		next_adjust_time = Simulator.CurrentRound + Config.sched.qosEpoch;
		for( int i = 0; i < Config.Ng; i++ )
		{
		    if( Simulator.QoSCtrl.is_HWA(i) )
//		    if( Simulator.network.nodes[i].cpu.is_HWA() )
		    {
			hwa_prior[hwa_cnt++] = i;
		    }
		}
		Array.Sort(hwa_prior, cmp_hwa_priority);
	    }
	    /*	    Console.Write("HWA_Prior:");
	    for( int i = 0; i< Config.HWANum; i++ )
	    {
		Console.Write(",{0}", hwa_prior[Config.HWANum-i-1]);
	    }
	    Console.Write("\n");
	    */
	}
	override public int getPriority( int id )
	{
	    if( Config.sched.static_prior_gpu_is_lower_cpu &&
		Simulator.network.nodes[id].cpu.is_GPU() )
		return 0;
	    else if( Simulator.QoSCtrl.is_HWA(id) )
//	    else if( Simulator.network.nodes[id].cpu.is_HWA() )	    
		return(2+Array.IndexOf(hwa_prior,id));
	    else
		return 1;
	}
	override public int getPriority( SchedBuf tgt )
	{
	    return(getPriority(tgt.mreq.request.requesterID));
	}

	override protected void schedMaskPrepare()
	{
	    if(( chan.HWAUnIssueRequests > 0 ) && Config.sched.hwa_str_priority )
	    {
		base.schedMaskPrepare();
	    }
	    else
	    {
		for( int pid = 0; pid < Config.Ng; pid++ )
		{
		    schedMask[pid] = true;
		}
		for( int i = 0; i < Config.memory.numBanks; i++ )
		{
		    bank_reserve[i] = int.MaxValue;
		    bank_reserved_rowhit[i] = false;
		    bank_reserve_priority[i] = -1;
		}
		data_bus_reserved_priority = -1;
	    }

	    return;
	}

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
	    {
                winner = candidate;
 		return winner;
	    }

	    if( (!Simulator.QoSCtrl.is_HWA(winner.mreq.request.requesterID) ) &&
		(!Simulator.QoSCtrl.is_HWA(candidate.mreq.request.requesterID) ))
//	    if( (!Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA()) &&
//		(!Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA()))
	    {
		MemoryRequest req1 = winner.mreq;
		MemoryRequest req2 = candidate.mreq;
		int rank1 = rank[req1.request.requesterID];
		int rank2 = rank[req2.request.requesterID];
		if (rank1 != rank2) {
		    if (rank1 > rank2) return winner;
		    else return candidate;
		}
	    }
	    else if( !Simulator.QoSCtrl.is_HWA(winner.mreq.request.requesterID) )
//	    else if( !Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() )
	    {
		return candidate;
	    }
	    else if( !Simulator.QoSCtrl.is_HWA(candidate.mreq.request.requesterID) )
//	    else if( !Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA() )
	    {
		return winner;
	    }
	    else
	    {
		if( winner.mreq.request.requesterID == candidate.mreq.request.requesterID )
		{
		    if(candidate.IsOlderThan(winner))
		    {
			return candidate;				
		    }
		    else
		    {
			return winner;
		    }
		}
		else if( getPriority(winner.mreq.request.requesterID) < getPriority(candidate.mreq.request.requesterID))
		    return candidate;
		else
		    return winner;
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

	private void buf_stat_out()
	{
            for(int i=0;i<buf.Length;i++)
            {
                if(buf[i].Valid && buf[i].moreCommands && !buf[i].Busy)
                {
		    req_num[buf[i].mreq.request.requesterID]++;
		}
		if(buf[i].Valid)
		{
		    buf_num[buf[i].mreq.request.requesterID]++;
		}
	    }
	    Console.Write("{0}:", chan.mem_id);
	    for(int i=0;i<Config.Ng;i++)
	    {
		Console.Write(",{0}", req_num[i]);
		req_num[i] = 0;
	    }
	    Console.Write("\n");
	    Console.Write("{0}:", chan.mem_id);
	    for(int i=0;i<Config.Ng;i++)
	    {
		Console.Write(",{0}", buf_num[i]);
		buf_num[i] = 0;
	    }
	    Console.Write("\n");
	}
        
        public override void Tick()
        {
            base.Tick();

	    /*	    
	    log_cnt++;
	    if( log_cnt >= 500 )
	    {
		buf_stat_out();
		log_cnt = 0;
	    }
	    */
	    if( winner != null )
	    {
		if( !winner.moreCommands )
		{
		    int id = winner.mreq.request.requesterID;
		    if( Simulator.QoSCtrl.is_HWA(id) )		    
//		    if( Simulator.network.nodes[id].cpu.is_HWA() )
		    {
			memreq_cnt[id]++;
			/*
			Console.Write("{0}:,time:{1},", chan.mem_id, Simulator.CurrentRound );
			for( int i = 0; i < Config.Ng; i++ )
			{
			    if( Simulator.network.nodes[i].cpu.is_HWA() )
				Console.Write("{0},", memreq_cnt[i] );
			}
			Console.Write("\n");
			*/
		    }
		}
	    }
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
//		if( !Simulator.network.nodes[p].cpu.is_HWA() )
		if( !Simulator.QoSCtrl.is_HWA(p) )
		{
		    if(rank[p]>=icluster_size)
			Simulator.stats.TimeInLowCluster[p].Add();
		    else
			Simulator.stats.TimeInHighCluster[p].Add();
		}
            }

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
		if( Simulator.network.nodes[p].cpu.is_GPU() )
		    cache_miss = Simulator.stats.dramreqs_persrc[p].Count;		    
                if(p==(Config.Ng-1))
                    cache_miss = Simulator.stats.dramreqs_persrc[p].Count;
                ulong delta_cache_miss = cache_miss - prev_cache_miss[p];
                prev_cache_miss[p] = cache_miss;

                ulong inst_cnt = Simulator.stats.insns_persrc[p].Count;
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
            for (int p = 0; p < Config.Ng; p++)
		rank[p] = Array.IndexOf(tids, p);

            //cluster
            int ncluster_size = 0;
            double service_total = 0;
            double service_runsum = 0;

            for (int p = 0; p < Config.Ng; p++)
//		if( !Simulator.network.nodes[p].cpu.is_HWA() ) // without HWA
		    service_total += service[p];

            for (int r = Config.Ng - 1; r >= 0; r--) {
                int pid = Array.IndexOf(rank, r);
		if( !Simulator.QoSCtrl.is_HWA(pid) )
//		if( !Simulator.network.nodes[pid].cpu.is_HWA() )
		{
		    service_runsum += service[pid];
		    if (service_runsum > Config.sched.AS_cluster_factor * service_total)
			break;

		    ncluster_size++;
		}
            }

            return Config.Ng - ncluster_size - Config.HWANum;
        }

        public void shuffle()
        {
            SchedTCM.ShuffleAlgo shuffle_algo = Config.sched.shuffle_algo;
            if (Config.sched.is_adaptive_shuffle) {

                double blp_thresh = Config.sched.adaptive_threshold * Config.memory.numBanks;
                double rbl_thresh = Config.sched.adaptive_threshold;
                if (blp_diff > blp_thresh && rbl_diff > rbl_thresh) {
                    shuffle_algo = SchedTCM.ShuffleAlgo.Hanoi;
                }
                else {
                    shuffle_algo = SchedTCM.ShuffleAlgo.ControlledRandom;
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
                case SchedTCM.ShuffleAlgo.Naive:
                    for (int r = Config.HWANum; r < icluster_size+Config.HWANum; r++) { // HWAs are allocated to the lowest rank
                        int pid = pids[r];
                        rank[pid] = (((r-Config.HWANum) + (icluster_size - 1)) % icluster_size ) + Config.HWANum;
                    }
                    break;

                case SchedTCM.ShuffleAlgo.ControlledRandom:
                    int step = icluster_size / 2 + 1;
                    for (int r = Config.HWANum; r < icluster_size+Config.HWANum; r++) {
                        int pid = pids[r];
                        rank[pid] = ((r-Config.HWANum) + step) % icluster_size + Config.HWANum;
                    }
                    break;

                case SchedTCM.ShuffleAlgo.Random:
                    for (int r = icluster_size+Config.HWANum - 1; r > Config.HWANum; r--) {
                        int pid1 = Array.IndexOf(rank, r);

                        int chosen_r = rand.Next(Config.HWANum,r + 1);
                        int chosen_pid = Array.IndexOf(rank, chosen_r);

                        rank[pid1] = chosen_r;
                        rank[chosen_pid] = r;
                    }
                    break;

                case SchedTCM.ShuffleAlgo.Hanoi:
                    int even = 2 * icluster_size;
                    int phase = shuffle_cnt % even;

                    if (phase < icluster_size) {
                        int grab_rank = (icluster_size - 1) - phase + Config.HWANum;
			if( grab_rank >= Config.Ng ) 
			    Console.WriteLine("grab rank is over(0) icluster_size:{0}, phase:{1}, HWANum:{2}", icluster_size, phase, Config.HWANum );
                        int grab_pid = Array.IndexOf(rank, grab_rank);
                        rank[grab_pid] = -1;

			if( icluster_size+Config.HWANum >= Config.Ng )
			    Console.WriteLine("rank is over(1) icluster_size:{0}, phase:{1}, HWANum:{2}, grab_rank:{3} ", icluster_size, phase, Config.HWANum, grab_rank );

                        for (int r = grab_rank + 1; r <= icluster_size+Config.HWANum - 1; r++) {
                            int pid = Array.IndexOf(rank, r);
                            rank[pid] = r - 1;
                        }
                        rank[grab_pid] = icluster_size+Config.HWANum - 1;
                    }
                    else {
                        int grab_rank = (icluster_size - 1) + Config.HWANum;
			if( grab_rank >= Config.Ng ) 
			    Console.WriteLine("grab rank is over(2) icluster_size:{0}, phase:{1}, HWANum:{2}", icluster_size, phase, Config.HWANum );

                        int grab_pid = Array.IndexOf(rank, grab_rank);
                        rank[grab_pid] = -1;

                        for (int r = grab_rank - 1; r >= ((phase - 1) % icluster_size)+Config.HWANum; r--) {
			    if( r >= Config.Ng )
				Console.WriteLine("rank is over(2) icluster_size:{0}, phase:{1}, HWANum:{2}, grab_rank:{3}, r:{4} ", icluster_size, phase, Config.HWANum, grab_rank,r );
                            int pid = Array.IndexOf(rank, r);
                            rank[pid] = r + 1;
                        }
                        rank[grab_pid] = ((phase - 1) % icluster_size)+Config.HWANum;
                    }
                    break;
            }

            //sanity check
            for (int r = 0; r < Config.Ng; r++) {
                int pid = Array.IndexOf(rank, r);
                Debug.Assert(pid != -1);
            }

	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
//		Debug.Assert((rank[pid]>=Config.HWANum) || Simulator.network.nodes[pid].cpu.is_HWA());
		Debug.Assert((rank[pid]>=Config.HWANum) || Simulator.QoSCtrl.is_HWA(pid));		
	    }

	    //HWA check
	    for( int r = 0; r < Config.HWANum; r++ )
	    {
		int pid = Array.IndexOf(rank,r);
//		Debug.Assert(Simulator.network.nodes[pid].cpu.is_HWA());
		Debug.Assert(Simulator.QoSCtrl.is_HWA(pid));
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
                icluster_pids[r] = Array.IndexOf(rank, r+Config.HWANum);
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
                nice_rank[pid] = r + Config.HWANum;
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
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
//		Debug.Assert((rank[pid]>=Config.HWANum) || Simulator.network.nodes[pid].cpu.is_HWA());
		Debug.Assert((rank[pid]>=Config.HWANum) || Simulator.QoSCtrl.is_HWA(pid));
	    }
	    for( int r = 0; r < Config.HWANum; r++ )
	    {
		int pid = Array.IndexOf(rank,r);
//		Debug.Assert(Simulator.network.nodes[pid].cpu.is_HWA());
		Debug.Assert(Simulator.QoSCtrl.is_HWA(pid));
	    }
        }

        public int sort_mpki(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
	    //hwa is always the lowest rank/hwas are allocated to icluster
            if (pid1 == pid2) return 0;
            
            double mpki1 = mpki[pid1];
            double mpki2 = mpki[pid2];

//	    if (Simulator.network.nodes[pid1].cpu.is_HWA() &&
//		(!Simulator.network.nodes[pid2].cpu.is_HWA()) ) return -1;
//	    else if( (!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
//		     Simulator.network.nodes[pid2].cpu.is_HWA() ) return 1; 
//	    else if( (Simulator.network.nodes[pid1].cpu.is_HWA()) &&
//		      Simulator.network.nodes[pid2].cpu.is_HWA() ) return -1; 
	    if ( Simulator.QoSCtrl.is_HWA(pid1) &&
		 !Simulator.QoSCtrl.is_HWA(pid2) ) return -1;
	    if ( !Simulator.QoSCtrl.is_HWA(pid1) &&
		 Simulator.QoSCtrl.is_HWA(pid2) ) return 1;
	    if ( Simulator.QoSCtrl.is_HWA(pid1) &&
		 Simulator.QoSCtrl.is_HWA(pid2) ) return -1;

	    else if (mpki1 < mpki2) return 1;
            else return -1;
        }

        public int sort_rbl(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
	    //hwa must not be included in pid1/pid2
            if (pid1 == pid2) return 0;

            double rbl1 = rbl[pid1];
            double rbl2 = rbl[pid2];

//	    Debug.Assert((!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
//			 (!Simulator.network.nodes[pid2].cpu.is_HWA()));
	    Debug.Assert((!Simulator.QoSCtrl.is_HWA(pid1)) &&
			 (!Simulator.QoSCtrl.is_HWA(pid2)));

            if (rbl1 < rbl2) return 1;
            else return -1;
        }

        public int sort_blp(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
	    //hwa must not be included in pid1/pid2
            if (pid1 == pid2) return 0;

            double blp1 = blp[pid1];
            double blp2 = blp[pid2];

//	    Debug.Assert((!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
//			 (!Simulator.network.nodes[pid2].cpu.is_HWA()));
	    Debug.Assert((!Simulator.QoSCtrl.is_HWA(pid1)) &&
			 (!Simulator.QoSCtrl.is_HWA(pid2)));

            if (blp1 > blp2) return 1;
            else return -1;
        }

        public int sort_nice(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
	    //hwa must not be included in pid1/pid2
            int nice1 = nice[pid1];
            int nice2 = nice[pid2];

//	    Debug.Assert((!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
//			 (!Simulator.network.nodes[pid2].cpu.is_HWA()));
  	    Debug.Assert((!Simulator.QoSCtrl.is_HWA(pid1)) &&
			 (!Simulator.QoSCtrl.is_HWA(pid2)));

            if (nice1 != nice2) {
                if (nice1 > nice2) return 1;
                else return -1;
            }
            return 0;
        }

	public int cmp_hwa_priority( int pid1, int pid2 )
	{
	    if( Config.sched.is_hwa_sched_rm ) // Rate Monotonic
	    {
		ulong period1 = Simulator.network.nodes[pid1].cpu.deadLine;
		ulong period2 = Simulator.network.nodes[pid2].cpu.deadLine;
		if( period1 == period2 )
		{
		    ulong remaining_workload1 = Simulator.network.nodes[pid1].cpu.deadLineReq - Simulator.network.nodes[pid1].cpu.deadLineReqCnt;
		    ulong remaining_workload2 = Simulator.network.nodes[pid2].cpu.deadLineReq - Simulator.network.nodes[pid2].cpu.deadLineReqCnt;
		    if( remaining_workload1 == remaining_workload2 ) return 0;
		    if( remaining_workload1 > remaining_workload2 ) return 1;
		    else return -1;
		}
		else if( period1 < period2 ) return 1;
		else return -1;
	    }
	    else if( Config.sched.is_hwa_sched_wkld ) // Based Workload Progress
	    {
		double progress1 = Simulator.QoSCtrl.getWorkloadProgress(pid1);
		double progress2 = Simulator.QoSCtrl.getWorkloadProgress(pid2);
		if( progress1 == progress2 )
		{	    
		    ulong remaining_workload1 = Simulator.network.nodes[pid1].cpu.deadLineReq - Simulator.network.nodes[pid1].cpu.deadLineReqCnt;
		    ulong remaining_workload2 = Simulator.network.nodes[pid2].cpu.deadLineReq - Simulator.network.nodes[pid2].cpu.deadLineReqCnt;
		    if( remaining_workload1 == remaining_workload2 ) return 0;
		    if( remaining_workload1 > remaining_workload2 ) return 1;
		    else return -1;
		}
		else if( progress1 < progress2 ) return 1;
		else return -1;
	    }
	    else // Early Deadline First
	    {
		long remaining_time1 = (long)remainingTime(pid1);
		long remaining_time2 = (long)remainingTime(pid2);
		if( remaining_time1 == remaining_time2 )
		{
		    ulong remaining_workload1 = Simulator.network.nodes[pid1].cpu.deadLineReq - Simulator.network.nodes[pid1].cpu.deadLineReqCnt;
		    ulong remaining_workload2 = Simulator.network.nodes[pid2].cpu.deadLineReq - Simulator.network.nodes[pid2].cpu.deadLineReqCnt;
		    if( remaining_workload1 == remaining_workload2 ) return 0;
		    if( remaining_workload1 > remaining_workload2 ) return 1;
		    else return -1;
		}
		else if( remaining_time1 < remaining_time2 ) return 1;
		else return -1;
	    }
	}

	/*	
	public int cmp_hwa_priority( int pid1, int pid2 )
	{
	    long remaining_time1 = remainingTime(pid1);
	    long remaining_time2 = remainingTime(pid2);
	    if( remaining_time1 == remaining_time2 )
	    {
		long remaining_workload1 = (long)Simulator.network.nodes[pid1].cpu.deadLineReq - (long)Simulator.network.nodes[pid1].cpu.deadLineReqCnt;
		long remaining_workload2 = (long)Simulator.network.nodes[pid2].cpu.deadLineReq - (long)Simulator.network.nodes[pid2].cpu.deadLineReqCnt;
		if( remaining_workload1 == remaining_workload2 ) return 0;
		if( remaining_workload1 > remaining_workload2 ) return 1;
		else return -1;
	    }
	    else if( remaining_time1 < remaining_time2 ) return 1;
	    else return -1;
	}
	*/
    }
}
