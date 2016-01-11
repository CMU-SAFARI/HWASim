using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class DualSlackSchedule : Scheduler
    {
	// mpki
	public double[] mpki;
	public ulong[] prev_cache_miss;
	public ulong[] prev_inst_cnt;
	
	// quantum
	public int quantum_cnt;
	public int quantum_cycles_left;

	// bandwidth allocate
	public static bool allocate_fin;
	public static double effective_req_sum;
	public static int shared_req_per_core;
	public static int[] bw_required;
	public static int[] bw_allocated;
	public static int[] bw_consumed;
	public static int[] rank;
	public int[] bw_consumed_per_sched;

	// top index number in schedBuf per core 
	public ulong[] oldest_when_arrived;
	public int[] top_index_in_buf;

	public int[] priority;
//	public bool[] schedMask;
//	public int[] bank_reserve;
//	public int   data_bus_reserved_priority;
//	public bool[] bank_reserved_rowhit;

	public DualSlackSchedule(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
	{
	    mpki = new double[Config.Ng];
            prev_cache_miss = new ulong[Config.Ng];
            prev_inst_cnt = new ulong[Config.Ng];
	    top_index_in_buf = new int[Config.Ng];
	    oldest_when_arrived = new ulong[Config.Ng];

            quantum_cycles_left = Config.sched.quantum_cycles;

	    if( bw_required == null )
		bw_required = new int[Config.Ng];
	    if( bw_allocated == null )
		bw_allocated = new int[Config.Ng];
	    if( bw_consumed == null )
		bw_consumed = new int[Config.Ng];
	    if( rank == null )
		rank = new int[Config.Ng];
	    bw_consumed_per_sched = new int[Config.Ng];

	    if( shared_req_per_core == 0 )
		get_effective_req_sum();

	    priority = new int[Config.Ng];
//	    schedMask = new bool[Config.Ng];
//	    bank_reserve = new int[Config.memory.numBanks];
//	    bank_reserved_rowhit = new bool[Config.memory.numBanks];
//	    data_bus_reserved_priority = 0;

	}

	public void get_effective_req_sum()
	{
	    // calculate effective_req_sum
	    string [] deadLineList = Config.hwaDeadLineList.Split(',');
	    string [] deadLineReqCntList = Config.hwaDeadLineReqCntList.Split(',');	    
	    if ((deadLineList.Length != Config.N) || (deadLineReqCntList.Length != Config.N))
		throw new Exception(String.Format("Invalid deadline list. Need to match # of nodes: {0}", Config.N));		

	    double req_per_cycle_sum = 0.0;
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		ulong deadLine = Convert.ToUInt64(deadLineList[i]);
		ulong deadLineReq = Convert.ToUInt64(deadLineReqCntList[i]);
		if( deadLine != 0 )
		    req_per_cycle_sum += (double)deadLineReq/(double)deadLine;
	    }
	    double total_req_in_quantum = (double)Config.sched.quantum_cycles /(double)4 * Config.sched.effective_bw_ratio 
		* Config.memory.numMemControllers * Config.memory.numChannels;

	    double total_hwareq_in_quantum = (double)Config.sched.quantum_cycles * (double)Config.memory.busRatio * req_per_cycle_sum;

	    effective_req_sum = total_req_in_quantum - total_hwareq_in_quantum;
	    shared_req_per_core = (int)Math.Floor(effective_req_sum * Config.sched.sharedThreshold / ( Config.Ng - Config.HWANum));

	    Console.WriteLine("EffectiveBW(All):{0}GB/s, BWfromHWA:{1}GB/s, BWfromCPU:{2}GB/s, BWfromeachCore:{3}GB-{4}/s",
			      total_req_in_quantum*64/(Config.sched.quantum_cycles*4)*2666666667/1024/1024/1024,
			      total_hwareq_in_quantum*64/(Config.sched.quantum_cycles*4)*2666666667/1024/1024/1024,
			      effective_req_sum*64/(Config.sched.quantum_cycles*4)*2666666667/1024/1024/1024,
			      (double)shared_req_per_core*64/(Config.sched.quantum_cycles*4)*2666666667/1024/1024/1024,
			      shared_req_per_core);
	    

	    return;
	}
	    
	public void allocate_bandwidth()
	{
	    if( allocate_fin ) return;
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
		if( delta_inst_cnt == 0 )
		    curr_mpki = mpki[p]; // use previous value

		Console.WriteLine("mpki({0}):{1},{2},{3}",p,curr_mpki,delta_cache_miss,delta_inst_cnt);
                //GPU
                mpki[p] = Config.sched.history_weight * mpki[p] + (1 - Config.sched.history_weight) * curr_mpki;
	    }

	    long cpu_inst_in_quantum = Config.sched.quantum_cycles * Config.memory.busRatio * Config.proc.instructionsPerCycle;
	    long gpu_inst_in_quantum = Config.sched.quantum_cycles * Config.memory.busRatio * Config.proc.GPUinstructionsPerCycle;

	    Console.WriteLine("Allocate");

	    int bw_sum = 0;
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		if( Simulator.network.nodes[i].cpu.is_HWA() ) bw_required[i] = 0;
		else if( Simulator.network.nodes[i].cpu.is_GPU() ) bw_required[i] = (int)Math.Floor(mpki[i] * (double)gpu_inst_in_quantum / 1000);
		else bw_required[i] = (int)Math.Floor(mpki[i] * (double)cpu_inst_in_quantum / 1000);

		rank[i] = i;

		Console.WriteLine("id:{0}, mpki:{1}, bw_required:{2}, bw_consumed_prev:{3}/{4}", i, mpki[i], bw_required[i], bw_consumed[i], bw_allocated[i] );
		if( !Simulator.network.nodes[1].cpu.is_HWA() )
		    bw_sum += bw_consumed[i];
	    }
	    Console.WriteLine("Prev Consumed BW-{0}GB/s", (double)bw_sum*64/(Config.sched.quantum_cycles*4)*2666666667/1024/1024/1024);
	    // adjust
	    effective_req_sum = effective_req_sum * Config.sched.history_weight + (1-Config.sched.history_weight)*bw_sum;
	    shared_req_per_core = (int)Math.Floor(effective_req_sum * Config.sched.sharedThreshold / ( Config.Ng - Config.HWANum));	    

	    int effective_req_sum_tmp = (int)Math.Floor(effective_req_sum);

	    for( int i = 0; i < Config.Ng; i++ )
	    {
		if( !Simulator.network.nodes[i].cpu.is_HWA() )
		{		
		    if(( shared_req_per_core > bw_required[i] ) && ( bw_required[i] > 0 ))
		    {
			effective_req_sum_tmp -= bw_required[i];
			bw_allocated[i] = bw_required[i];
		    }
		    else
		    {
			effective_req_sum_tmp -= shared_req_per_core;
			bw_allocated[i] = shared_req_per_core;
		    }
		}
		bw_consumed[i] = 0;
	    }

	    if( effective_req_sum_tmp > 0 )
	    {
		Array.Sort(rank,cmp_bw_required);
		for( int r = Config.Ng-1; r >= 0; r-- )
		{
		    int idx = rank[r];
		    if( !Simulator.network.nodes[idx].cpu.is_HWA() )
		    {	
			if( bw_required[idx] > bw_allocated[idx] )
			{
			    if( bw_required[idx] - bw_allocated[idx] <= effective_req_sum_tmp )
			    {
				effective_req_sum_tmp -= bw_required[idx] - bw_allocated[idx];
				bw_allocated[idx] = bw_required[idx] ; 
			    }
			    else
			    {
				bw_allocated[idx] += effective_req_sum_tmp;
				effective_req_sum_tmp = 0;
			    }
			}
			if( effective_req_sum_tmp == 0 )
			{
			    break;
			}
		    }
		}
	    }
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		Console.WriteLine("id:{0}, allocated:{1}/required:{2}", i, bw_allocated[i], bw_required[i] );
	    }

	    allocate_fin =true;
	}

	override protected void schedMaskPrepare()
	{
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		oldest_when_arrived[i] = ulong.MaxValue;
		top_index_in_buf[i] = -1;
	    }
	    for( int i = 0; i < buf.Length; i++ )
	    {
		if(buf[i].Valid && buf[i].moreCommands && !buf[i].Busy)
		{
		    int req_id = buf[i].mreq.request.requesterID;
		    if( oldest_when_arrived[req_id] > buf[i].whenArrived )
		    {
			oldest_when_arrived[req_id] = buf[i].whenArrived;
			top_index_in_buf[req_id] = buf[i].index;
		    }
		}
	    }
	    base.schedMaskPrepare();

	    return;
	}

        // Override this for other algorithms
	override public void calculate_priority()
	{
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		if( Simulator.network.nodes[i].cpu.is_HWA() )
		{
		    if( Simulator.QoSCtrl.schedule_ready(i,chan.mem_id, chan.id) ) 
			priority[i] = Config.HWANum+2+Simulator.QoSCtrl.getHwaRank(i);
		    else 
			priority[i] = Simulator.QoSCtrl.getHwaRank(i);
		}
		else if( bw_consumed[i] >= bw_allocated[i] )
		{ 
		    if( priority[i] != Config.HWANum )
		    {
			Console.WriteLine("ID:{0}, Target bandwidth is consumed", i);
		    }
		    priority[i] = Config.HWANum;
		    
		}
		else priority[i] = Config.HWANum+1; 
	    }

	    return;
	}
	
	override public int getPriority( int id )
	{
	    return(priority[id]);
	}
	    
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
	{
            if(winner == null)
                winner = candidate;
	    else
	    {
		int winner_id = winner.mreq.request.requesterID;
		int candidate_id = candidate.mreq.request.requesterID;
		int winner_priority = getPriority(winner_id);
		int candidate_priority = getPriority(candidate_id);

		if( winner_priority < candidate_priority )
		{
		    winner = candidate;
		}
		else if( winner_priority == candidate_priority )
		{
		    if( Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() &&
			Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA() )
		    {
			int winner_rank = Simulator.QoSCtrl.getHwaRank(winner.mreq.request.requesterID);
			int candidate_rank = Simulator.QoSCtrl.getHwaRank(candidate.mreq.request.requesterID);
			if( winner_rank > candidate_rank )
			    return winner;
			else if( winner_rank < candidate_rank )
			    return candidate;
		    }
		    if(( winner.index == top_index_in_buf[winner_id] ) &&
		       ( candidate.index != top_index_in_buf[candidate_id] ))
			return winner;
		    else if(( winner.index != top_index_in_buf[winner_id] ) &&
			    ( candidate.index == top_index_in_buf[candidate_id] ))
			return candidate;

		    if( winner_id != candidate_id )
		    {
			if( bw_required[winner_id] <= bw_required[candidate_id] ) return winner;
			return candidate;
		    }

		    if(!winner.Urgent && candidate.Urgent)
			winner = candidate;
		    else if(winner.Urgent && candidate.Urgent && candidate.IsOlderThan(winner))
			winner = candidate;
		    else if(winner.Urgent && !candidate.Urgent)
			winner = winner;
		    else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
			winner = candidate;
		    else if(winner.IsRowBufferHit && !candidate.IsRowBufferHit )
			winner = winner;
		    else if(candidate.IsOlderThan(winner))
			winner = candidate;
		}		    
	    }
	    return winner;
	}
	public override void Tick()
	{
	    base.Tick();

	    if( winner != null )
		if( !winner.moreCommands )
		{
		    bw_consumed[winner.mreq.request.requesterID]++;
		    bw_consumed_per_sched[winner.mreq.request.requesterID]++;
		}

	    if( quantum_cycles_left > 0 )
	    { 
		allocate_fin = false;
		quantum_cycles_left--;
		return;
	    }
	    quantum_cnt++;
            quantum_cycles_left = Config.sched.quantum_cycles;	    

	    allocate_bandwidth();

	    for( int i = 0; i < Config.Ng; i++ )
	    {
		Console.WriteLine("id:{0}, bw_consumed_per_sched:{1}", i, bw_consumed_per_sched[i] );
		bw_consumed_per_sched[i] = 0;
	    }
	    

	}

	public int cmp_bw_required( int pid1, int pid2)
	{
	    if( bw_required[pid1] < bw_required[pid2] ) return 1;
	    else if( bw_required[pid1] == bw_required[pid2] ) return 0;
	    else return -1;
	}
    }
}