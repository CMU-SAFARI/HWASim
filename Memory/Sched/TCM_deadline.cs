//#define FIXHWAID

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedTCMDeadLine : Scheduler
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

	public int catchflag;

	public bool   all_wait_flag;
	public int    max_hwa_rank;

	public int[] hwa_prior;
	public int[] deadline_prior;
	public ulong next_adjust_time;

	public int tmp_cnt;

	public int[] mem_intensity_req_cnt;
	public int[] mem_nonintensity_req_cnt;
	public bool[] next_cnt_disable;

	public bool log_already_output;

        public SchedTCMDeadLine(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
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

	    Console.WriteLine("TCM Parameter Quantum:{0}, shuffle:{1}", quantum_cycles_left, shuffle_cycles_left );
	    Console.WriteLine("Exception Check!!");
	    catchflag = 0;

	    hwa_prior = new int[Config.HWANum];
	    deadline_prior = new int[Config.Ng];

	    mem_intensity_req_cnt = new int[Config.Ng];
	    mem_nonintensity_req_cnt = new int[Config.Ng];
	    next_cnt_disable = new bool[Config.Ng];

	    for( int i = 0; i < Config.Ng; i++ )
	    {
		mem_intensity_req_cnt[i] = 0;
		mem_nonintensity_req_cnt[i] = 0;
		next_cnt_disable[i] = false;
	    }

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
	    int next_priority;
	    int prior_sum_id = 0;

	    if(( Simulator.CurrentRound < next_adjust_time ) && ( Config.sched.qosEpoch > 0 ))
		return;

	    next_adjust_time = Simulator.CurrentRound + Config.sched.qosEpoch;

	    log_already_output = false;

	    for( int i = 0; i < Config.Ng; i++ )
	    {
		if( Simulator.network.nodes[i].cpu.is_HWA() )
		{
		    hwa_prior[hwa_cnt++] = i;
		    prior_sum_id = prior_sum_id << 2;
		    if( !next_cnt_disable[i] )
		    {	
			if( deadline_prior[i] == 2 ) // high priority
			    prior_sum_id+=2;
			else
			    prior_sum_id+=1;
		    }
		}
		next_priority = deadlinePriority(i);
		if( next_priority != deadline_prior[i] )
		{
//		    Console.WriteLine("HWA {0}: change priority from {1} to {2}", i, deadline_prior[i], deadlinePriority(i));
#if LOG_DETAIL
		    if( Config.sched.workload_pred_perctrl )
			Console.WriteLine("HWA {0}: prior (mid:{3}) {1}->{2} in {4}", i, deadline_prior[i], next_priority, chan.mem_id, Simulator.CurrentRound );
		    else if( chan.mem_id == 0 )
			Console.WriteLine("HWA {0}: prior {1}->{2} in {3}", i, deadline_prior[i], next_priority, Simulator.CurrentRound);
#endif
		}
		if( Simulator.network.nodes[i].cpu.is_HWA() )
		{
		    if( Simulator.network.nodes[i].cpu.deadLineReqCnt < Simulator.network.nodes[i].cpu.deadLineReq )
		    {	
			next_cnt_disable[i] = false;
			if( next_priority == 0 )
			    Simulator.stats.HWACycleInLowPrior[i].Add((ulong)Config.sched.qosEpoch);
			else if ( next_priority == 1 )
			    Simulator.stats.HWACycleInMediumPrior[i].Add((ulong)Config.sched.qosEpoch);
			else
			    Simulator.stats.HWACycleInHighPrior[i].Add((ulong)Config.sched.qosEpoch);
		    }
		    else
			next_cnt_disable[i] = true;
		}

		deadline_prior[i] = next_priority;
	    }
	    Array.Sort(hwa_prior, cmp_hwa_priority);

#if LOG_DETAIL
#else
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		Simulator.QoSCtrl.req_cnt_nonint_based_prior[i,prior_sum_id] += (ulong)mem_nonintensity_req_cnt[i];
		Simulator.QoSCtrl.req_cnt_int_based_prior[i,prior_sum_id] += (ulong)mem_intensity_req_cnt[i];		
		mem_nonintensity_req_cnt[i] = 0;
		mem_intensity_req_cnt[i] = 0;
	    }
#endif

	    /*
	    Console.Write("HWA_Prior:");
	    for( int i = 0; i< Config.HWANum; i++ )
	    {
//		Console.Write(",{0}", hwa_prior[Config.HWANum-i-1]);
		Console.Write(",{0}", getPriority(16+i) );
	    }
	    Console.Write("\n");
	    */
	}
	
	override public int getPriority( int id )
	{
	    if( Simulator.network.nodes[id].cpu.is_HWA() )
	    {
		if( deadline_prior[id] == 1 ) // same as CPU (not used)
		    return(Config.HWANum);
		else if( deadline_prior[id] == 2 ) // greater than CPU
		    return(Config.HWANum+1+Array.IndexOf(hwa_prior,id));
		else // less than CPU
		    return(Array.IndexOf(hwa_prior,id));
	    }
	    else
		return(Config.HWANum);
	}
	override public int getPriority( SchedBuf tgt )
	{
	    return(getPriority(tgt.mreq.request.requesterID));
	}

	private int deadlinePriority( SchedBuf tgt )
	{
	    return(deadlinePriority(tgt.mreq.request.requesterID));
	}

	private int deadlinePriority( int id )
	{
	    if( !Simulator.network.nodes[id].cpu.is_HWA() )
		{
		    
		    return 1;
		}
	    if(( Simulator.network.nodes[id].cpu.deadLineReq == 0 ) ||
	       ( Simulator.network.nodes[id].cpu.deadLine == 0 )){
		return 1;

	    }
	    double progress;
	    double target_progress;
	    if( Config.sched.workload_pred_perctrl )
	    {
		progress = Simulator.QoSCtrl.getWorkloadProgress(id,chan.mem_id);
	    }
	    else
	    {
		progress = (double)Simulator.network.nodes[id].cpu.deadLineReqCnt / 
		    (double)Simulator.network.nodes[id].cpu.deadLineReq;
	    }
	    target_progress = (double)( Simulator.CurrentRound - Simulator.network.nodes[id].cpu.deadLineCnt ) / 
		(double)Simulator.network.nodes[id].cpu.deadLine;
//	    double progress = (double)Simulator.network.nodes[id].cpu.deadLineReqCnt / 
//		(double)Simulator.network.nodes[id].cpu.deadLineReq;
//	    double target_progress = (double)( Simulator.CurrentRound - Simulator.network.nodes[id].cpu.deadLineCnt ) / 
//		(double)Simulator.network.nodes[id].cpu.deadLine;
#if LOG_DETAIL
#if FIXHWAID
	    if( id == 10 )
#else
	    if( !Simulator.QoSCtrl.schedule_tgt( id, chan.mem_id, chan.id ) && !log_already_output ) // not guaranteed latency cluster
#endif
	    {
		Console.Write("MemNonIntensive{0},",chan.mem_id );
		for( int i = 0; i < Config.Ng; i++ )
		{
		    Console.Write("{0},",mem_nonintensity_req_cnt[i]);
		}
		Console.Write("\n");
		Console.Write("MemIntensive{0},",chan.mem_id );
		for( int i = 0; i < Config.Ng; i++ )
		{
		    Console.Write("{0},",mem_intensity_req_cnt[i]);
		}
		Console.Write("\n");
		log_already_output = true;
	    }
#endif
#if LOG_DETAIL
#if FIXHWAID
	    if( id == 10 )
#else
	    if( !Simulator.QoSCtrl.schedule_tgt( id, chan.mem_id, chan.id ) ) // not guaranteed latency cluster
#endif
	    {
		if( Config.sched.workload_pred_perctrl )
		    Console.WriteLine("HWA {0}: progress {1} ({3}/{4}) / {2}", id, progress, target_progress, 
				      Simulator.QoSCtrl.HWAWorkLoadCtrl[chan.mem_id].getCurrent(id),
				      Simulator.QoSCtrl.HWAWorkLoadCtrl[chan.mem_id].getTarget(id) );
		else
		    Console.WriteLine("HWA {0}: progress {1} / {2}", id, progress, target_progress );
	    }
#endif		    
	    double emergency_progress;

	    if( Simulator.QoSCtrl.schedule_tgt( id, chan.mem_id, chan.id ) ) // guaranteed latency cluster
		emergency_progress = Config.sched.hwa_emergency_progress_short;
	    else 
		emergency_progress = Config.sched.hwa_emergency_progress_long;

	    if( target_progress > emergency_progress )
		return 2;
//	    if( target_progress > Config.sched.hwa_emergency_progress ) // 0.9 )
//		return 2;
	    else if( progress > target_progress )
		return 0;
	    else
		return 2;
	}

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
	    {
                winner = candidate;
 		return winner;
	    }
	    double winner_priority = getPriority( winner );
	    double candidate_priority = getPriority( candidate );

	    if( winner_priority < candidate_priority )
	    {
		winner = candidate;
		return winner;
	    }
	    else if( winner_priority > candidate_priority )
	    {
		return winner;
	    }
	    if( (!Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA()) &&
		(!Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA()))
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
	    else if( !Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() ) // winner is CPU, candidate is HWA
	    {
		MemoryRequest req1 = winner.mreq;
		int rank1 = rank[req1.request.requesterID];
		if( rank1 >= icluster_size + Config.HWANum )
		{
		    return winner;
		}
		else
		{
		    return candidate;
		}
	    }
	    else if( !Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA() ) // winner is HWA, candidate is CPU
	    {
		MemoryRequest req1 = candidate.mreq;
		int rank1 = rank[req1.request.requesterID];
		if( rank1 >= icluster_size + Config.HWANum )
		{
		    return candidate;
		}
		else
		{
		    return winner;
		}
	    }
	    else // both are HWA
	    {
		if( winner.mreq.request.requesterID == candidate.mreq.request.requesterID )
		{
		    if(candidate.IsOlderThan(winner))
			return candidate;				
		    else
			return winner;
		}

		if( remainingTime(winner.mreq.request.requesterID) > remainingTime(candidate.mreq.request.requesterID) )
		{
		    return candidate;
		}
		else
		{
		    return winner;
		}
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

	    /*
	    tmp_cnt++;
	    if( tmp_cnt == 25 )
	    {
		tmp_cnt = 0;
		Console.Write("Q({0}-{1}):",chan.mem_id, chan.id);
		for( int pid = 0; pid < Config.Ng; pid++ )
		{
		    Console.Write("{0},",chan.unIssueRequestsPerCore[pid] );
		}
		Console.Write("\n");
	    }
	    */
	    if( winner != null )
	    {
		int id = winner.mreq.request.requesterID;

		if( Simulator.network.nodes[id].cpu.is_HWA() )
		{    
		    if( !winner.moreCommands )
		    {
			if( getPriority(winner) > Config.HWANum ) // class 3 or 2
			{
			    Simulator.stats.HWAReqInHighPrior[id].Add();
			}
			else
			{
			    Simulator.stats.HWAReqInLowPrior[id].Add();
			}
			mem_intensity_req_cnt[id]++;
		    }
		}
		else
		{
		    if( !winner.moreCommands )
		    {
			int rank_winner = rank[id];
			if( rank_winner >= icluster_size + Config.HWANum ) // cpu is low memory intensity
			{
			    mem_nonintensity_req_cnt[id]++;
			}
			else
			{
			    mem_intensity_req_cnt[id]++;
			}
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

//	    Console.WriteLine("TCM:New Quantum");
            //new quantum
            decay_stats();

            quantum_cnt++;
            quantum_cycles_left = Config.sched.quantum_cycles;

            shuffle_cnt = 0;
            shuffle_cycles_left = Config.sched.shuffle_cycles;

            //cluster
            icluster_size = cluster();

//	    Console.WriteLine("icluster_size:{0}", icluster_size);
            if (icluster_size > 1) assign_nice_rank();

            for (int p = 0; p < Config.Ng; p++)
            {
		if( !Simulator.network.nodes[p].cpu.is_HWA() )
		{
		    if(rank[p]>=icluster_size+Config.HWANum)
		    {
//			Console.WriteLine("pid:{0}, LowCluster",p);
			Simulator.stats.TimeInLowCluster[p].Add();
		    }
		    else
		    {
//			Console.WriteLine("pid:{0}, HighCluster",p);
			Simulator.stats.TimeInHighCluster[p].Add();
		    }
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
		//bug of original code??
		ulong row_buffer_hits = Simulator.stats.dramreqs_persrc[p].Count - Simulator.stats.DRAMActivationsPerSrc[p].Count;
		ulong delta_row_buffer_hits = row_buffer_hits - shadow_row_hits[p];

//                double curr_rbl = ((double)shadow_row_hits[p]) / delta_cache_miss;
                double curr_rbl = ((double)delta_row_buffer_hits) / delta_cache_miss;
                rbl[p] = Config.sched.history_weight * rbl[p] + (1 - Config.sched.history_weight) * curr_rbl;
//		Console.WriteLine("Pid:{0}, RBL:{1}, dramreq:{2}, delta_cache_miss:{3}", p, rbl[p],Simulator.stats.dramreqs_persrc[p].Count,delta_cache_miss);
                shadow_row_hits[p] = row_buffer_hits;

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
	    {
		rank[p] = Array.IndexOf(tids, p);
//		Console.WriteLine("pid:{0}, Rank:{1}, mpki:{2}",p,rank[p],mpki[p]);
	    }

            //cluster
            int ncluster_size = 0;
            double service_total = 0;
            double service_runsum = 0;

            for (int p = 0; p < Config.Ng; p++)
	    {
//		if( !Simulator.network.nodes[p].cpu.is_HWA() ) // without HWA
//		    Console.WriteLine("Service pid:{0}={1}", p, service[p]);
		    service_total += service[p];
		
	    }

            for (int r = Config.Ng - 1; r >= 0; r--) {
                int pid = Array.IndexOf(rank, r);
		if( !Simulator.network.nodes[pid].cpu.is_HWA() )
		{
		    service_runsum += service[pid];
//		    Console.WriteLine("Check threshold rank:{0}, pid:{1}, service_sum:{2}, threshold:{3}", r, pid, service_runsum, Config.sched.AS_cluster_factor * service_total );
		    if (service_runsum > Config.sched.AS_cluster_factor * service_total)
			break;

		    ncluster_size++;
		}
            }

            return Config.Ng - ncluster_size - Config.HWANum;
        }

        public void shuffle()
        {
	    try
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
                        rank[pid] = (((r-Config.HWANum)+ (icluster_size - 1)) % icluster_size ) + Config.HWANum;
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
			if( grab_rank >= Config.Ng || catchflag == 1) 
			    Console.WriteLine("grab rank is over(0) icluster_size:{0}, phase:{1}, HWANum:{2}", icluster_size, phase, Config.HWANum );
                        int grab_pid = Array.IndexOf(rank, grab_rank);
                        rank[grab_pid] = -1;
//			Console.WriteLine("grab_rank_1:{0}(pid:{1})",grab_rank,grab_pid);

			if( icluster_size+Config.HWANum >= Config.Ng || catchflag == 1)
			    Console.WriteLine("rank is over(1) icluster_size:{0}, phase:{1}, HWANum:{2}, grab_rank:{3} ", icluster_size, phase, Config.HWANum, grab_rank );

                        for (int r = grab_rank + 1; r <= icluster_size+Config.HWANum - 1; r++) {
                            int pid = Array.IndexOf(rank, r);
                            rank[pid] = r - 1;
//			    Console.WriteLine("rank(pid:{0}):{1}->{2}",r,r-1);
                        }
                        rank[grab_pid] = icluster_size+Config.HWANum - 1;
//			Console.WriteLine("grab_rank:{0}",rank[grab_pid]);
                    }
                    else {
                        int grab_rank = (icluster_size - 1) + Config.HWANum;
//			Console.WriteLine("grab_rank_2:{0}",grab_rank);
			if( grab_rank >= Config.Ng || catchflag == 1 ) 
			    Console.WriteLine("grab rank is over(2) icluster_size:{0}, phase:{1}, HWANum:{2}", icluster_size, phase, Config.HWANum );
                        int grab_pid = Array.IndexOf(rank, grab_rank);
                        rank[grab_pid] = -1;
//			Console.WriteLine("grab_rank_1:{0}(pid:{1})",grab_rank,grab_pid);

                        for (int r = grab_rank - 1; r >= ((phase - 1) % icluster_size)+Config.HWANum; r--) {
			    if( r >= Config.Ng || catchflag == 1 )
				Console.WriteLine("rank is over(2) icluster_size:{0}, phase:{1}, HWANum:{2}, grab_rank:{3}, r:{4} ", icluster_size, phase, Config.HWANum, grab_rank,r );
                            int pid = Array.IndexOf(rank, r);
                            rank[pid] = r + 1;
//			    Console.WriteLine("rank(pid:{0}):{1}->{2}",r,r+1);
                        }
                        rank[grab_pid] = ((phase - 1) % icluster_size)+Config.HWANum;
//			Console.WriteLine("grab_rank:{0}",rank[grab_pid]);
                    }
                    break;
            }

            //sanity check
//	    Console.WriteLine("After Shuffle\n");
            for (int r = 0; r < Config.Ng; r++) {
                int pid = Array.IndexOf(rank, r);
//		Console.WriteLine("Rank{0}:pid={1}",r,pid);
                Debug.Assert(pid != -1);
            }
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		Debug.Assert((rank[pid]>=Config.HWANum) || Simulator.network.nodes[pid].cpu.is_HWA());
	    }
	    //HWA check
	    for( int r = 0; r < Config.HWANum; r++ )
	    {
		int pid = Array.IndexOf(rank,r);
		Debug.Assert(Simulator.network.nodes[pid].cpu.is_HWA());
	    }
	    }
	    catch
	    {
		Console.WriteLine("EXCEPTION!!!!! icluster_size:{0}, HWANum:{1}, shuffle_cnt:{2}", icluster_size, Config.HWANum, shuffle_cnt );
		for( int i = 0; i < Config.Ng; i++ )
		{
		    Console.WriteLine("Rank:{0}", rank[i]);
		}
		catchflag = 1;
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
		Debug.Assert((rank[pid]>=Config.HWANum) || Simulator.network.nodes[pid].cpu.is_HWA());
	    }
	    for( int r = 0; r < Config.HWANum; r++ )
	    {
		int pid = Array.IndexOf(rank,r);
		Debug.Assert(Simulator.network.nodes[pid].cpu.is_HWA());
	    }
        }

        public int sort_mpki(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
	    //hwa is always the lowest rank/hwas are allocated to icluster
            if (pid1 == pid2) return 0;
            
            double mpki1 = mpki[pid1];
            double mpki2 = mpki[pid2];

	    if (Simulator.network.nodes[pid1].cpu.is_HWA() &&
		(!Simulator.network.nodes[pid2].cpu.is_HWA()) ) return -1;
	    else if( (!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
		     Simulator.network.nodes[pid2].cpu.is_HWA() ) return 1; 
	    else if( (Simulator.network.nodes[pid1].cpu.is_HWA()) &&
		      Simulator.network.nodes[pid2].cpu.is_HWA() ) return -1; 
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

	    Debug.Assert((!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
			 (!Simulator.network.nodes[pid2].cpu.is_HWA()));
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

	    Debug.Assert((!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
			 (!Simulator.network.nodes[pid2].cpu.is_HWA()));
            if (blp1 > blp2) return 1;
            else return -1;
        }

        public int sort_nice(int pid1, int pid2)
        {
            //return 1 if first argument is "greater" (higher rank)
	    //hwa must not be included in pid1/pid2
            int nice1 = nice[pid1];
            int nice2 = nice[pid2];

	    Debug.Assert((!Simulator.network.nodes[pid1].cpu.is_HWA()) &&
			 (!Simulator.network.nodes[pid2].cpu.is_HWA()));
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

    }
}
