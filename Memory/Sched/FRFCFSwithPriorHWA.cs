using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedFRFCFSwithPriorHWA : Scheduler
    {
	public int[] hwa_prior;

        public SchedFRFCFSwithPriorHWA (SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
	    hwa_prior = new int[Config.HWANum];
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
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		if( Simulator.network.nodes[i].cpu.is_HWA() )
		{
		    hwa_prior[hwa_cnt++] = i;
		}
	    }
	    Array.Sort(hwa_prior, cmp_hwa_priority);
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
	    if( Simulator.network.nodes[id].cpu.is_HWA() )	    
		return(1+Array.IndexOf(hwa_prior,id));
	    else
		return 0;
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
                winner = candidate;
	    else
		{
		    if( Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() &&
			Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA() ){
			if( winner.mreq.request.requesterID == candidate.mreq.request.requesterID )
			{
			    if(candidate.IsOlderThan(winner))
			    {
				winner = candidate;				
			    }
			}
			else if( getPriority(winner.mreq.request.requesterID) < getPriority(candidate.mreq.request.requesterID))
			    winner = candidate;
//			else if( remainingTime(winner) > remainingTime(candidate) )
//			    winner = candidate;
		    }
		    else if( Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() )
		    {
			
		    }
		    else if( Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA() )
		    {
			winner = candidate;
		    }
		    else
		    {
			if(!winner.Urgent && candidate.Urgent)
			    winner = candidate;
			else if(winner.Urgent && candidate.Urgent && candidate.IsOlderThan(winner))
			    winner = candidate;
			else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
			    winner = candidate;
			else if(candidate.IsOlderThan(winner))
			    winner = candidate;
		    }
		}
	    return winner;
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
	    else // Early Deadline First
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
	}

    }
}
