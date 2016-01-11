using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedFRFCFSDeadLine : Scheduler
    {
	public int[] hwa_prior;
	public int[] deadline_prior;
	public ulong next_adjust_time;
//	public bool[] schedMask;
//	public int[] bank_reserve;
//	public int   data_bus_reserved_priority;
//	public bool[] bank_reserved_rowhit;

        public SchedFRFCFSDeadLine(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
	    hwa_prior = new int[Config.HWANum];
	    deadline_prior = new int[Config.Ng];
//	    schedMask = new bool[Config.Ng];
//	    bank_reserve = new int[Config.memory.numBanks];
//	    bank_reserved_rowhit = new bool[Config.memory.numBanks];
//	    data_bus_reserved_priority = 0;
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
	    if(( Simulator.CurrentRound < next_adjust_time ) && ( Config.sched.qosEpoch > 0 ))
		return;

	    next_adjust_time = Simulator.CurrentRound + Config.sched.qosEpoch;

	    for( int i = 0; i < Config.Ng; i++ )
	    {
//		if( Simulator.network.nodes[i].cpu.is_HWA() )
		if( Simulator.QoSCtrl.is_HWA(i) )
		{
		    hwa_prior[hwa_cnt++] = i;
		}
		deadline_prior[i] = deadlinePriority(i);
	    }
	    Array.Sort(hwa_prior, cmp_hwa_priority);
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
//	    if( Simulator.network.nodes[id].cpu.is_HWA() )
	    if( Simulator.QoSCtrl.is_HWA(id) )
	    {
		if( deadline_prior[id] == 1 ) // same as CPU
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
	    if( !Simulator.QoSCtrl.is_HWA(id) )
//	    if( !Simulator.network.nodes[id].cpu.is_HWA() )
		{
		    
		    return 1;
		}
	    if(( Simulator.network.nodes[id].cpu.deadLineReq == 0 ) ||
	       ( Simulator.network.nodes[id].cpu.deadLine == 0 )){
		return 1;

	    }
	    double progress = (double)Simulator.network.nodes[id].cpu.deadLineReqCnt / 
		(double)Simulator.network.nodes[id].cpu.deadLineReq;
	    double target_progress = (double)( Simulator.CurrentRound - Simulator.network.nodes[id].cpu.deadLineCnt ) / 
		(double)Simulator.network.nodes[id].cpu.deadLine;

	    double emergency_progress;

	    if( Simulator.network.nodes[id].cpu.emergentTh >= 0 )
		emergency_progress = Simulator.network.nodes[id].cpu.emergentTh;
	    else if( Simulator.QoSCtrl.schedule_tgt( id, chan.mem_id, chan.id ) ) // guaranteed latency cluster
		emergency_progress = Config.sched.hwa_emergency_progress_short;
	    else 
		emergency_progress = Config.sched.hwa_emergency_progress_long;

//	    if(( id == 8 ) || ( id == 11 ))
//	    if(( id == 9 ) || ( id == 10 ))
//	    {
//		emergency_progress = 0.8;
//	    }


//	    Console.WriteLine("emergency progress :{0} id:{1}", emergency_progress, id );

	    if( target_progress > emergency_progress )
		return 2;
	    else if( progress > target_progress )
		return 0;
	    else if( Config.sched.hwa_frfcfs_deadline_same_priority )
		return 1;
	    else
		return 2;
	    /*
	    else if( target_progress > 0.9 )
		{
		    return 2;
		}
	    else
		{
		    return 1;
		}
		*/
	}

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
                winner = candidate;
	    else
		{
		    int winner_priority = getPriority( winner.mreq.request.requesterID );
		    int candidate_priority = getPriority( candidate.mreq.request.requesterID );
		    if( winner_priority < candidate_priority )
			{
			    winner = candidate;
			}
		    else if( winner_priority == candidate_priority )
			{
			    
			    if( Simulator.QoSCtrl.is_HWA(winner.mreq.request.requesterID)  &&
				Simulator.QoSCtrl.is_HWA(candidate.mreq.request.requesterID) ){
//			    if( Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() &&
//				Simulator.network.nodes[candidate.mreq.request.requesterID].cpu.is_HWA() ){
				if( winner.mreq.request.requesterID == candidate.mreq.request.requesterID )
				{
				    if(candidate.IsOlderThan(winner))
				    {
					winner = candidate;				
				    }
				}
			    }

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

        public override void Tick()
        {

            base.Tick();

	    if( winner != null )
	    {
		int id = winner.mreq.request.requesterID;

//		if( Simulator.network.nodes[id].cpu.is_HWA() )
		if( Simulator.QoSCtrl.is_HWA(id) )
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
		    }
		}
	    }
	}

    }
}
