//#define FIXHWAID


using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class HWAWorkLoadCtrlPerCore
    {
	public SimplePredictor workLoadPredictor;
	public ulong predictedWorkLoad;
	public bool predictedEn;

	public ulong workLoadCnt;
	public ulong topAddress;

	public HWAWorkLoadCtrlPerCore()
	{
	    workLoadPredictor = new SimplePredictor( Config.sched.workload_pred_entry_num, (int)Math.Ceiling(Math.Log(Config.memory.busWidth,2.0))+3 );
	    predictedWorkLoad = 0;
	    predictedEn = false;
	    workLoadCnt = 0;
	}

	public void HWAWorkLoadEnqueue( ulong address, ulong def_workload )
	{
	    ulong cnt;
	    bool flag;

	    if( !predictedEn )
	    {	
		predictedEn = true;
		topAddress = address;

		flag = workLoadPredictor.getValue(address, out cnt );
		if( flag )
		    predictedWorkLoad = cnt;
		else
		    predictedWorkLoad = def_workload;
	    }
	}
	
	public void HWAWorkLoadIssue( ulong address )
	{
	    workLoadCnt++;
	}
	public void HWAWorkLoadPeriodStart()
	{
	    predictedEn = false;
	    if( workLoadCnt > 0 )
		workLoadPredictor.setValue(topAddress, workLoadCnt);
	    workLoadCnt = 0;
	}
	public ulong getCurrent()
	{
	    return(workLoadCnt);
	}

	public ulong getTarget()
	{
	    return(predictedWorkLoad);
	}

    }
    public class HWAWorkLoadCtrlPerMemC
    {
	public HWAWorkLoadCtrlPerCore[] HWAWorkLoadCtrlPerCore;

	public HWAWorkLoadCtrlPerMemC()
	{
	    HWAWorkLoadCtrlPerCore = new HWAWorkLoadCtrlPerCore[Config.Ng];
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		HWAWorkLoadCtrlPerCore[i] = new HWAWorkLoadCtrlPerCore();
	    }
	}

	public void HWAWorkLoadEnqueue( int pid, ulong address, ulong def_workload )
	{
	    HWAWorkLoadCtrlPerCore[pid].HWAWorkLoadEnqueue(address,def_workload);
	}
	public void HWAWorkLoadIssue( int pid, ulong address )
	{
	    HWAWorkLoadCtrlPerCore[pid].HWAWorkLoadIssue(address);
	}
	public void HWAWorkLoadPeriodStart( int pid )
	{
	    HWAWorkLoadCtrlPerCore[pid].HWAWorkLoadPeriodStart();
	}
	public ulong getCurrent(int pid )
	{
	    return(HWAWorkLoadCtrlPerCore[pid].getCurrent());
	}
	public ulong getTarget(int pid )
	{
	    return(HWAWorkLoadCtrlPerCore[pid].getTarget());
	}
    }

    public class RoundRobinScheduler
    {
	public int master_num;
	public int quantum_length;
	public int top_no;

	int quantum_cnt;

	public RoundRobinScheduler( int set_master_num, int set_quantum_length )
	{
	    master_num = set_master_num;
	    quantum_length = set_quantum_length;

	    quantum_cnt = 0;
	    top_no = 0;
	}

	public int tick()
	{
	    quantum_cnt++;
	    if( quantum_cnt == quantum_length )
	    {
		quantum_cnt = 0;
		top_no++;
		if( top_no == master_num ) 
		    top_no = 0;
	    }
	    return(top_no);
	}
	
	public int getRank( int no ) // lower is greater
	{
	    if( no == top_no )
	    {
		return(master_num-1);
	    }
	    if( no > top_no )
	    {
		return(master_num-1-(no-top_no));
	    }
	    else
	    {
		return(master_num-1-(master_num - (top_no - no)));
	    }
	}
    }

    public class HWAQoSCtrl
    {
	public ulong quantum;
	public ulong epoch;
	public int[] hwa_rm_rank; // rate_monotonic
	public int[] hwa_ed_rank; // early_deadline_first
	public int[] hwa_pid2hid;
	public ulong [] deadLine;
	public ulong [] deadLineReq;
	public int[] priority;
	public ulong[] consumed_bw;
	public ulong[,] consumed_bw_per_ch;

	public ulong quantum_left;
	public ulong epoch_left;
	
	protected int numChannels;
	protected int numMemCtrls;
	protected int numBanks;

	Random cRandom;
	int rnd_value;
	
	public HWAWorkLoadCtrlPerMemC [] HWAWorkLoadCtrl;
	public ulong[,] last_page;
	public ulong[,] req_cnt;
	public ulong[,] rowhit_cnt;
	
	public RoundRobinScheduler rr_scheduler;

	public ulong[,] req_cnt_int_based_prior;
	public ulong[,] req_cnt_nonint_based_prior;
	public ulong[,] dram_bank_req_cnt;
	public ulong[] dram_bank_req_cnt_base;
	public ulong[] RequestsPerBank;
	public ulong[] CPURequestsPerBank;
	public int[] bank_order;

	public HWAQoSCtrl()
	{
	    hwa_rm_rank = new int[Config.HWANum];
	    hwa_ed_rank = new int[Config.HWANum];
	    hwa_pid2hid = new int[Config.HWANum];
	    deadLine = new ulong[Config.Ng];
	    deadLineReq = new ulong[Config.Ng];
	    priority = new int[Config.Ng];

	    numChannels = Config.memory.numChannels;
	    numMemCtrls = Config.memory.numMemControllers;
	    numBanks = Config.memory.numBanks;
	    consumed_bw = new ulong[Config.Ng];
	    consumed_bw_per_ch = new ulong[Config.Ng,numChannels*numMemCtrls];
	    last_page = new ulong[Config.Ng,numChannels*numMemCtrls];
	    req_cnt = new ulong[Config.Ng,numChannels*numMemCtrls];
	    rowhit_cnt = new ulong[Config.Ng,numChannels*numMemCtrls];

	    req_cnt_int_based_prior = new ulong[Config.Ng,(int)Math.Pow(4,Config.HWANum)];
	    req_cnt_nonint_based_prior = new ulong[Config.Ng,(int)Math.Pow(4,Config.HWANum)];

	    dram_bank_req_cnt = new ulong[numChannels*numMemCtrls,numBanks];
	    dram_bank_req_cnt_base = new ulong[numChannels*numMemCtrls];
	    RequestsPerBank = new ulong[numBanks];
	    CPURequestsPerBank = new ulong[numBanks];
	    bank_order = new int[numBanks];

	    quantum = Config.sched.qosQuantum;
	    epoch = Config.sched.qosEpoch;
	    quantum_left = quantum;
	    epoch_left = epoch;


	    cRandom = new System.Random();

	    HWAWorkLoadCtrl = new HWAWorkLoadCtrlPerMemC[numMemCtrls];

	    rr_scheduler = new RoundRobinScheduler(Config.Ng-Config.HWANum, Config.sched.roundrobin_quantum);

	    string [] deadLineList = Config.hwaDeadLineList.Split(',');
	    string [] deadLineReqCntList = Config.hwaDeadLineReqCntList.Split(',');	    
	    if ((deadLineList.Length != Config.N) || (deadLineReqCntList.Length != Config.N))
		throw new Exception(String.Format("Invalid deadline list. Need to match # of nodes: {0}", Config.N));		

	    int hwa_cnt = 0;
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		deadLine[pid] = Convert.ToUInt64(deadLineList[pid]);
		deadLineReq[pid] = Convert.ToUInt64(deadLineReqCntList[pid]);

		String filename = Simulator.network.workload.getFile(pid);
		if( filename.Contains("HWA") ||
		    ( Config.GPUasHWA && ( filename.Contains("GAME") || filename.Contains("BENCH")) ) ) // Accelerator
//		if( deadLine[pid] > 0 )
		{
		    if( hwa_cnt >= Config.HWANum )
			throw new Exception(String.Format("Config.HWANum({0}) is less than the number of deadlines defined in hwaDeadLineList ({1})", Config.HWANum, hwa_cnt + 1));
		    hwa_pid2hid[hwa_cnt] = pid;
		    hwa_cnt++;
		}

		consumed_bw[pid] = 0;
		for( int mid = 0; mid < numMemCtrls*numChannels; mid++ )
		{
		    consumed_bw_per_ch[pid,mid] = 0;
		}
	    }
	    for( int mid = 0; mid < numMemCtrls; mid++ )
	    {
		HWAWorkLoadCtrl[mid] = new HWAWorkLoadCtrlPerMemC();
	    }
	    Array.Copy(hwa_pid2hid,hwa_rm_rank,Config.HWANum);
	    Array.Copy(hwa_pid2hid,hwa_ed_rank,Config.HWANum);
	    Array.Sort(hwa_rm_rank, sort_period);

	    
	}
	public void result_out()
	{
	    Console.WriteLine("memory intensive\n");
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		Console.Write("{0},", pid);
		for( int pri_id = 0; pri_id < (int)Math.Pow(4,Config.HWANum); pri_id++ )
		{
		    Console.Write("{0},", req_cnt_int_based_prior[pid,pri_id]);
		}
		Console.Write("\n");
	    }
	    Console.WriteLine("memory nonintensive\n");
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		Console.Write("{0},", pid);
		for( int pri_id = 0; pri_id < (int)Math.Pow(4,Config.HWANum); pri_id++ )
		{
		    Console.Write("{0},", req_cnt_nonint_based_prior[pid,pri_id]);
		}
		Console.Write("\n");
	    }
	    Console.WriteLine("buf_req_cnt_per_bank\n");
	    for( int ctrl_id = 0; ctrl_id < numMemCtrls; ctrl_id++ )
	    {
		for( int ch_id = 0; ch_id < numChannels; ch_id++ )
		{
		    Console.Write("ctrl:{0}, ch:{1}, ", ctrl_id, ch_id );
		    for( int bank_id = 0; bank_id < numBanks; bank_id++ )
		    {
			Console.Write("{0},", (double)dram_bank_req_cnt[numChannels*ctrl_id+ch_id,bank_id] / (double)dram_bank_req_cnt_base[numChannels*ctrl_id+ch_id] );
		    }
		    Console.Write("\n");
		}
	    }
	}
	public int getReqMinBank(int rank)
	{
	    for( int i = 0; i < numBanks; i++ )
	    {
		bank_order[i] = i;
	    }
	    /*	    if( rank == 0 )
	    {
		Console.Write("bank_check:");
		for( int i = 0; i < numBanks; i++ )
		    Console.Write("{0},", RequestsPerBank[i]);
		Console.Write("\n");
	    }*/
	    Array.Sort(bank_order, sort_bank_req);
	    return(bank_order[rank]);
	}
	public int sort_bank_req(int bid1, int bid2)
	{
	    ulong req_bank1 = RequestsPerBank[bid1];
	    ulong req_bank2 = RequestsPerBank[bid2];
	    if( req_bank1 == req_bank2 )
		return 0;
	    else if( req_bank1 < req_bank2 ) 
		return -1;
	    else
		return 1;
	}
	public int sort_period(int pid1, int pid2)
	{
	    ulong period1 = deadLine[pid1];
	    ulong period2 = deadLine[pid2];
	    if( period1 == period2 ) 
		return 0;
//	    {
//		if( pid1 < pid2 ) return 1;
//		else return -1;
//	    }
	    else if( period1 < period2 ) return 1;
	    else return -1;
	}
	public long remainingTime(int pid)
	{
	    long time;

	    time = (long)deadLine[pid] + (long)Simulator.network.nodes[pid].cpu.deadLineCnt - (long)Simulator.CurrentRound;
	    return time;
	}
	public ulong getDeadLineRound(int pid)
	{
	    return (deadLine[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt);
	}
	public bool isEmergency(int pid, double rate = 0.9)
	{
	    return((Simulator.CurrentRound-Simulator.network.nodes[pid].cpu.deadLineCnt)/deadLine[pid] > rate);
	}
	public double getWorkloadProgress(int pid)
	{
	    double progress = (double)Simulator.network.nodes[pid].cpu.deadLineReqCnt / 
		(double)deadLineReq[pid];

	    return(progress);
//	    return(((double)Simulator.CurrentRound-(double)Simulator.network.nodes[pid].cpu.deadLineCnt)/(double)deadLine[pid]);
	}
	public double getWorkloadProgress(int pid, int mid )
	{
	    if( Config.sched.workload_pred_perctrl )
	    {
		ulong current = HWAWorkLoadCtrl[mid].getCurrent(pid);
		ulong tgt;
		if( Config.sched.workload_pred_worst )
		    tgt = deadLineReq[pid];
		else
		    tgt = HWAWorkLoadCtrl[mid].getTarget(pid);

		double progress = (double)current/(double)tgt;

		return(progress);
	    }
	    else
	    {
		return(getWorkloadProgress(pid));
	    }
	}

	public int sort_remainingTime(int pid1, int pid2)
	{
	    long time1 = remainingTime(pid1);
	    long time2 = remainingTime(pid2);
	    if( time1 == time2 ) return 0;
	    else if( time1 < time2 ) return 1;
	    else return -1;
	}
	    
	public int get_hwa_rank_base(int pid)
	{
	    if( Config.sched.is_hwa_sched_rm )  // Rate Monotonic
		return(get_hwa_rm_rank(pid));
	    else // EDF
	    {
		return(get_hwa_ed_rank(pid));
	    }
	}
	public int get_hwa_rm_rank( int pid )
	{
//	    Array.Sort(hwa_rm_rank, sort_period);	    
	    return(Array.IndexOf(hwa_rm_rank,pid));
	}
	public int get_hwa_ed_rank( int pid )
	{
	    Array.Sort(hwa_ed_rank, sort_remainingTime);
	    return(Array.IndexOf(hwa_ed_rank,pid));
	}

	public int sort_hwa_rank(int pid1, int pid2 )
	{
	    if( Config.sched.is_hwa_sched_rm ) return(sort_period(pid1,pid2));
	    else return(sort_remainingTime(pid1,pid2));
	}
	public int getRandomValue()
	{
	    return(rnd_value);
	}
	public bool is_HWA( int pid )
	{
	    return( Simulator.network.nodes[pid].cpu.is_HWA() ||
		    ( Simulator.network.nodes[pid].cpu.is_GPU() && Config.GPUasHWA ));
	}

	virtual public void schedule_each_quantum()
	{
	    return;
	}
	virtual public void adjust_each_epoch()
	{
	    return;
	}
	virtual public void schedule_each_deadline(int id, Trace m_trace )
	{
	    for( int mid = 0; mid < numMemCtrls; mid++ )
	    {
		/*
		if( id == 10 )
		    Console.WriteLine("Id:{0}, mid:{1}, pred:{2}, actual:{3}, idx:{4}, add:{5:x}", id, mid, 
				      HWAWorkLoadCtrl[mid].HWAWorkLoadCtrlPerCore[id].predictedWorkLoad, 
				      HWAWorkLoadCtrl[mid].HWAWorkLoadCtrlPerCore[id].workLoadCnt,
				      HWAWorkLoadCtrl[mid].HWAWorkLoadCtrlPerCore[id].workLoadPredictor.getEntry(HWAWorkLoadCtrl[mid].HWAWorkLoadCtrlPerCore[id].topAddress),
				      HWAWorkLoadCtrl[mid].HWAWorkLoadCtrlPerCore[id].topAddress);*/
		HWAWorkLoadCtrl[mid].HWAWorkLoadPeriodStart(id);
	    }
	    
	    return;
	}
	virtual public bool schedule_ready(int pid, int mid, int cid)
	{
	    return true;
	}
	virtual public bool schedule_tgt(int pid, int mid, int cid)
	{
	    return true;
	}
	virtual public bool schedule_all_cluster_check( int clst_id, int mid, int cid )
	{
	    return true;
	}
	virtual public bool schedule_cluster_check( int clst_id, int pid, int mid, int cid )
	{
	    return true;
	}
	virtual public void schedule_cluster_set( int clst_id, int pid, int mid, int cid, bool flag )
	{
	    return;
	}
	virtual public void setSuspendLLClstAccel()
	{
	    return;
	}
	virtual public void unsetSuspendLLClstAccel()
	{
	    return;
	}
	virtual public bool isSuspendLLClstAccel()
	{
	    return false;
	}
	virtual public int getHwaRank(int pid)
	{
	    return(get_hwa_rank_base(pid));
	}
	virtual public int getHwaId(int pid)
	{
	    return(Array.IndexOf(hwa_pid2hid,pid));
	}
	virtual public void calculate_priority(int mid, int cid)
	{
	    for( int i = 0; i < Config.Ng; i++ )
		priority[i] = 0;
	}
	virtual public int get_priority(int pid, bool marked)
	{
	    return(priority[pid]);
	}
	virtual public void bw_increment(int pid, int ctrl_id, int ch_id )
	{
	    consumed_bw[pid]++;
	    consumed_bw_per_ch[pid,ctrl_id*numChannels+ch_id]++;
	}
	virtual public void mem_req_enqueue( int pid, ulong address, int ctrl_id )
	{
	    HWAWorkLoadCtrl[ctrl_id].HWAWorkLoadEnqueue(pid,address,deadLineReq[pid]);
	    
	    check_rowbuffer_hit(pid,address,ctrl_id);
	}
	public void check_rowbuffer_hit( int pid, ulong address, int ctrl_id )
	{
	    ulong s_row;
	    int mem_idx, ch_idx, rank_idx, bank_idx, row_idx;
	    MemoryRequest.mapAddr(pid,address>>Config.cache_block, out s_row, out mem_idx, out ch_idx, out rank_idx, out bank_idx, out row_idx );

	    if( s_row == last_page[pid,ctrl_id] )
		rowhit_cnt[pid,ctrl_id]++;
	    
	    req_cnt[pid,ctrl_id]++;
	    
	    last_page[pid,ctrl_id] = s_row;

	    return;
	}
	public void rowbuffer_hit_reset( int ctrl_id )
	{
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		req_cnt[i,ctrl_id] = 0;
		rowhit_cnt[i,ctrl_id] = 0;
	    }
	    return;
	}
	public double get_rowbuffer_hit_rate( int pid, int ctrl_id )
	{
	    return((double)rowhit_cnt[pid,ctrl_id]/(double)req_cnt[pid,ctrl_id]);
	}
	virtual public void mem_req_issue( int pid, ulong address, int ctrl_id )
	{
	    HWAWorkLoadCtrl[ctrl_id].HWAWorkLoadIssue(pid,address);
	}
	virtual public void issue_request(int pid, int ctrl_id, int ch_id, bool isImmediate )
	{
	    return;
	}
	virtual public void set_execution_time( int pid, ulong time )
	{
	    return;
	}
	virtual public void deadline_update( int pid, ulong new_deadLine, ulong new_deadLineReq )
	{
	    deadLine[pid] = new_deadLine;
	    deadLineReq[pid] = new_deadLineReq;
	    Array.Sort(hwa_rm_rank, sort_period);
	}
	virtual public void Tick()
	{
	    rnd_value = cRandom.Next(0,100);

	    if( epoch > 0 )
	    {
		if( epoch_left != 0 )
		{
		    epoch_left--;
		}
		else
		{
		    adjust_each_epoch();
		    epoch_left = epoch;
		}
	    }
	    if( quantum > 0 )
	    {
		if( quantum_left != 0 )
		{
		    quantum_left--;
		}
		else
		{
		    schedule_each_quantum();
		    quantum_left = quantum;
		}
	    }
	}
    }

    public class QoSDeadLineCluster : HWAQoSCtrl
    {
	int min_deadLine_pid;
	ulong[] exe_inst_num;
	ulong[] required_time;
	ulong[] start_time;
	protected uint cRC;
	bool[] is_guaranteed_deadline;
	bool[] is_based_quantum;
	ulong quantum_start;
	public bool[] is_llclst_accelerated;
	public bool is_all_llclst_accelerated;
	bool is_suspend_llclst_accelerated;
	public int[] counter_for_accelerating_clst;
	PhasePredictor[] predictor;
	ulong[] sum_time;
	ulong[] first_address;
	ulong[] predicted_start_time;

	public QoSDeadLineCluster()
	{
	    cRC = Config.memory.cRC;
	    exe_inst_num = new ulong[Config.Ng];
	    required_time = new ulong[Config.Ng];
	    start_time = new ulong[Config.Ng];
	    is_guaranteed_deadline = new bool[Config.Ng];
	    is_based_quantum = new bool[Config.Ng];
//	    quantum_start = new ulong[Config.Ng];
	    is_llclst_accelerated = new bool[Config.Ng];
	    counter_for_accelerating_clst = new int[Config.Ng];

	    predictor = new PhasePredictor[Config.Ng];
	    sum_time = new ulong[Config.Ng];
	    first_address = new ulong[Config.Ng];
	    predicted_start_time = new ulong[Config.Ng];

	    min_deadLine_pid = -1;
	    quantum = 0;
	    quantum_start = 0;

	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		if(( deadLine[pid] != 0 ) &&
		   (( quantum == 0 ) || ( deadLine[pid] < quantum )))
		{
		    min_deadLine_pid = pid;
		    quantum = deadLine[pid];
		}
		is_guaranteed_deadline[pid] = false;
		is_llclst_accelerated[pid] = Config.sched.is_always_llclst_accelerated;
		counter_for_accelerating_clst[pid] = 0;
	    }	    
	    is_all_llclst_accelerated = Config.sched.is_always_llclst_accelerated;
	    is_suspend_llclst_accelerated = false;
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
//		if( deadLine[pid] == 0 )
//		    exe_inst_num[pid] = 0;
//		else
//		    exe_inst_num[pid] = (ulong)Math.Ceiling((double)deadLineReq[pid]/(double)deadLine[pid] * (double)quantum );

		exe_inst_num[pid] = deadLineReq[pid];
		required_time[pid] = get_required_time(pid,exe_inst_num[pid]);
		start_time[pid] = 0;

		predictor[pid] = new PhasePredictor( Config.sched.phase_pred_entry_num, required_time[pid], (int)Math.Ceiling(Math.Log(Config.memory.busWidth,2.0))+3 );
		sum_time[pid] = cRC;
	    }
	    calculate_start_time();
	}

	public ulong get_required_time( int pid, ulong exe_inst_num )
	{
	    if( exe_inst_num == 0 ) return 0;
	    if( Config.sched.qosWorstCaseStrict )
		return(cRC * exe_inst_num);
	    else
	    {
		if( pid == 17 )
		    return(cRC * exe_inst_num);
		else if( pid == 18 )
		    return(cRC * 3 + 4 * 4 * exe_inst_num);
		else
		    return(cRC + 4 * 4 * exe_inst_num);
	    }
	    
	}

	public void calculate_start_time()
	{
	    ulong end_time = quantum;

	    Console.WriteLine("New Quantum:{0}", quantum);

	    for( int r = Config.HWANum-1; r >= 0; r-- )
	    {
		int pid = hwa_rm_rank[r];
		if( pid >= 0 )
		{
		    is_guaranteed_deadline[pid] = false;		    
		    is_based_quantum[pid] = false; // synchronize own period
		    if( Config.sched.is_sdllclst_used & ( deadLine[pid] < 30000 )) // short_deadline
		    {
			if( Config.sched.is_extend_overwrap_sdllclst )
			{
			    ulong sum_other_worst_exe_time = 0;
			    for( int other_r = r + 1; other_r < Config.HWANum; other_r++ )
			    {
				ulong other_worst_exe_time;
				int other_pid = hwa_rm_rank[other_r];
				ulong other_deadline = deadLine[other_pid];
				other_worst_exe_time = (ulong)Math.Ceiling((double)required_time[pid]/(double)other_deadline) * required_time[other_pid];
				sum_other_worst_exe_time += other_worst_exe_time;
				Console.WriteLine("SumUp for id:{0}, worst_own:{1}, deadline:{2}, normalized:{3}, result:{4}", 
						  other_pid, required_time[other_pid], other_deadline, required_time[pid], other_worst_exe_time);
			    }
			    if( deadLine[pid] < required_time[pid] + sum_other_worst_exe_time )
				start_time[pid] = 0;
			    else
				start_time[pid] = deadLine[pid] - required_time[pid] - sum_other_worst_exe_time;
			    is_guaranteed_deadline[pid] = true;
			}
			else
			{
			    is_guaranteed_deadline[pid] = true;
			    if( deadLine[pid] < required_time[pid] )
				start_time[pid] = 0;
			    else
				start_time[pid] = deadLine[pid] - required_time[pid];
			}
		    }
		    Console.WriteLine("Id:{0},StartTime:{1}/DeadLine:{2},Required:{3},guaranteed:{4}", pid,start_time[pid], deadLine[pid], required_time[pid], is_guaranteed_deadline[pid]);

//		    Console.WriteLine("Id:{0},StartTime:{1},guaranteed:{2},based_quantum:{3},required:{4}", pid, start_time[pid],is_guaranteed_deadline[pid],is_based_quantum[pid],required_time[pid]);
		}
	    }
	}
	/*
	public void calculate_start_time()
	{
	    ulong end_time = quantum;

	    Console.WriteLine("New Quantum:{0}", quantum);

	    for( int r = Config.HWANum-1; r >= 0; r-- )
	    {
		int pid = hwa_rm_rank[r];
		if( pid >= 0 )
		{    
		    is_guaranteed_deadline[pid] = false;
		    if( end_time < required_time[pid] )
		    {
			start_time[pid] = 0;
			predicted_start_time[pid] = 0;
			end_time = 0;
			if( deadLine[pid] < 10000 )
			{
			    is_guaranteed_deadline[pid] = Config.sched.is_sdllclst_used;
			}
		    }
		    else
		    {
			start_time[pid] = end_time - required_time[pid];
//			is_guaranteed_deadline[pid] = true;
			is_guaranteed_deadline[pid] = Config.sched.is_sdllclst_used;
//			if( deadLine[pid] < quantum * 2 )
//			{
			    is_based_quantum[pid] = false;
			    ulong required_time_modified = (ulong)Math.Ceiling(( quantum - start_time[pid] ) * (double)deadLine[pid] / (double)quantum);
			    start_time[pid] = deadLine[pid] - required_time_modified;
//			}
//			else
//			    is_based_quantum[pid] = true;
			end_time -= required_time[pid];
			predicted_start_time[pid] = start_time[pid];
		    }
		    Console.WriteLine("Id:{0},StartTime:{1},guaranteed:{2},based_quantum:{3},required:{4}", pid, start_time[pid],is_guaranteed_deadline[pid],is_based_quantum[pid],required_time[pid]);
		}
	    }
	}
*/
	public bool is_min_deadline(int pid)
	{
	    return (pid==min_deadLine_pid);
	}

	override public void schedule_each_deadline(int id, Trace m_trace)
	{
	    base.schedule_each_deadline(id,m_trace);

	    is_llclst_accelerated[id] = Config.sched.is_always_llclst_accelerated;
	    counter_for_accelerating_clst[id] = 0;

	    if( Config.sched.is_always_llclst_accelerated )
		is_all_llclst_accelerated = true;
	    else
	    {
		is_all_llclst_accelerated = false;
		for( int i = 0; i < Config.Ng; i++ )
		    if( is_llclst_accelerated[i] ) is_all_llclst_accelerated = true;
	    }
#if FIXHWAID
	    if( id == 10 )
#endif
//		Console.WriteLine("HWA id:{0}, ll_cluster_accel:{1}, all:{2}", id, is_llclst_accelerated[id], is_all_llclst_accelerated);

	    predictor[id].setMaxLatency( first_address[id], sum_time[id] );
	    /*
#if FIXHWAID
	    if( id == 9 )
#endif
		Console.WriteLine("HWA id:{2}, pred_set addr:{0:x}, time:{1}", first_address[id], sum_time[id], id );
		*/
	    sum_time[id] = cRC;
	    first_address[id] = m_trace.address;

	    if( is_guaranteed_deadline[id] )
	    {
		/*
		ulong predicted_latency = predictor[id].getMaxLatency( first_address[id] );
		ulong predicted_required_time = (ulong)Math.Ceiling((double)predicted_latency * (double)quantum / (double)deadLine[id] );
		if( predicted_required_time < required_time[id] )
		{
		    if( is_based_quantum[id] )
		    {	
			predicted_start_time[id] = start_time[id] + ( required_time[id] - predicted_required_time );
		    }
		    else
		    {
			predicted_start_time[id] = start_time[id] + ( required_time[id] - predicted_required_time );
			ulong predicted_required_time_modified = (ulong)Math.Ceiling(( quantum - predicted_start_time[id] ) * (double)deadLine[id] / (double)quantum);
			predicted_start_time[id] = deadLine[id] - predicted_required_time_modified; 
		    }
		}
		else
		{
		*/
		predicted_start_time[id] = start_time[id];
//		}
//		if( id == 9 )
//		    Console.WriteLine("pred_next start:{0}, predict:{1}", start_time[id], predicted_start_time[id] );
	    }
	    
	    if( !is_min_deadline(id) ) return;
	    
	    quantum_start = Simulator.CurrentRound;

	    return;
	}
	override public bool schedule_all_cluster_check( int clst_id, int mid, int cid )
	{
	    return (is_all_llclst_accelerated && !is_suspend_llclst_accelerated );
	}
	override public bool schedule_cluster_check( int clst_id, int pid, int mid, int cid )
	{
	    if( clst_id == 0 )
	    {
		if( is_based_quantum[pid] )
		{
		    if( getDeadLineRound(pid) < quantum_start + quantum ) return true;
		    if( Simulator.CurrentRound > predicted_start_time[pid] + quantum_start ) return true;
		    else if( Simulator.CurrentRound > ( predicted_start_time[pid] + quantum_start - cRC )) return true;
		    else return false;
		}
		else
		{
		    if( Simulator.CurrentRound > predicted_start_time[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt ) return true;
		    else if( Simulator.CurrentRound > ( predicted_start_time[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt - cRC )) return true;
		    else return false;
		}
	    }
	    else
		return (is_llclst_accelerated[pid] & !is_suspend_llclst_accelerated);
	}
	override public void schedule_cluster_set( int clst_id, int pid, int mid, int cid, bool flag )
	{
	    if( flag & !is_llclst_accelerated[pid] )
	    {
		counter_for_accelerating_clst[pid]++;
		if( counter_for_accelerating_clst[pid] < Config.sched.threshold_for_accel_llclst )
		{
		    Console.WriteLine("HWA {0}:, ll_clster_cnt_for_accel:{1}/{2}", pid, counter_for_accelerating_clst[pid], Config.sched.threshold_for_accel_llclst );
		    return;
		}
		else
		    counter_for_accelerating_clst[pid] = 0;
	    }
	    is_llclst_accelerated[pid] = flag;

	    if( flag )
		is_all_llclst_accelerated = true;
	    else
	    {
		is_all_llclst_accelerated = false;
		for( int i = 0; i < Config.Ng; i++ )
		    if( is_llclst_accelerated[i] ) is_all_llclst_accelerated = true;
	    }
	    
#if FIXHWAID
	    if( pid == 10 )
#endif
//		Console.WriteLine("HWA {0}:, ll_cluster_accel:{1}, all:{2}", pid, flag, is_all_llclst_accelerated);
	    return;
	}

	public override bool schedule_ready( int pid, int mid, int cid )
	{
	    if( is_based_quantum[pid] )
	    {
		if( getDeadLineRound(pid) < quantum_start + quantum ) return true;
		if( Simulator.CurrentRound > start_time[pid] + quantum_start ) return true;
		else if( Simulator.CurrentRound > ( start_time[pid] + quantum_start - cRC )) return true;
		else return false;
	    }
	    else
	    {
		if( Simulator.CurrentRound > start_time[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt ) return true;
		else if( Simulator.CurrentRound > ( start_time[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt - cRC )) return true;
		else return false;
	    }
	}

	public override bool schedule_tgt( int pid, int mid, int cid )
	{
	    return(is_guaranteed_deadline[pid]);
	}
	public override void setSuspendLLClstAccel()
	{
	    is_suspend_llclst_accelerated = true;
	    return;
	}
	public override void unsetSuspendLLClstAccel()
	{
	    is_suspend_llclst_accelerated = false;
	    return;
	}
	public override bool isSuspendLLClstAccel()
	{
	    return is_suspend_llclst_accelerated;
	}

	public override int getHwaRank( int pid )
	{
	    return(get_hwa_rm_rank(pid));
	}
	public override int getHwaId( int pid )
	{
	    return(base.getHwaId(pid));
	}

	public override void set_execution_time( int pid, ulong time )
	{
	    if( time > cRC )
		sum_time[pid] += cRC;
	    else
		sum_time[pid] += time;
//	    if( pid == 9 )
//		Console.WriteLine("pred_sumup {0}, {1}, {2}", sum_time[pid],time, cRC);
	}
	public override void deadline_update( int pid, ulong new_deadLine, ulong new_deadLineReq )
	{
	    base.deadline_update(pid,new_deadLine,new_deadLineReq);
	    if( quantum > new_deadLine )
	    {
		quantum = new_deadLine;
		min_deadLine_pid = pid;
	    }
	    for( int id = 0; id < Config.Ng; id++ )
	    {
//		if( deadLine[id] == 0 )
//		    exe_inst_num[id] = 0;
//		else
//		    exe_inst_num[id] = (ulong)Math.Ceiling((double)deadLineReq[id]/(double)deadLine[id] * (double)quantum );
		exe_inst_num[id] = deadLineReq[id];

		required_time[id] = get_required_time(id,exe_inst_num[id]);
		start_time[id] = 0;
		sum_time[id] = cRC;
	    }
	    calculate_start_time();
	}
    }

    public class QoSCalcDelay : HWAQoSCtrl
    {
	int min_deadLine_pid;
	ulong[,] start_time;
	ulong[] tgt_inst_num;
	ulong[] pre_inst_num;
	ulong[] pre_deadLinePassNum;
	ulong[] inst_num_this_period;
	ulong[] required_time;
	protected uint cRC;
	ulong quantum_end;
	ulong[,] requested_num_per_ch;
	ulong[,] requested_num_other_ch;
	bool[,] is_scheduled;
	bool[] is_enable_schedule;
	
	public QoSCalcDelay()
	{
	    start_time = new ulong[Config.Ng,numMemCtrls*numChannels];
	    tgt_inst_num = new ulong[Config.Ng];
	    pre_inst_num = new ulong[Config.Ng];
	    pre_deadLinePassNum = new ulong[Config.Ng];
	    inst_num_this_period = new ulong[Config.Ng];
	    required_time = new ulong[Config.Ng];
	    cRC = Config.memory.cRC;
	    requested_num_per_ch = new ulong[Config.Ng,numMemCtrls*numChannels];
	    requested_num_other_ch = new ulong[Config.Ng,numMemCtrls*numChannels];
	    is_scheduled = new bool[Config.Ng,numMemCtrls*numChannels];
	    is_enable_schedule = new bool[numMemCtrls*numChannels];

	    string [] deadLineList = Config.hwaDeadLineList.Split(',');
	    if (deadLineList.Length > 1 && deadLineList.Length < Config.N)
		throw new Exception(String.Format("Invalid deadline list. Need to match # of nodes: {0}", Config.N));

//	    quantum = Config.sched.qosQuantum;

	    min_deadLine_pid = -1;
	    quantum = 0;

	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		if(( deadLine[pid] != 0 ) &&
		   (( quantum == 0 ) || ( deadLine[pid] < quantum )))
		{
		    min_deadLine_pid = pid;
		    quantum = deadLine[pid];
		}
		tgt_inst_num[pid] = 0;
		required_time[pid] = 0;
		pre_inst_num[pid] = 0;
		for( int mid = 0; mid < numMemCtrls*numChannels; mid++ )
		{
		    requested_num_per_ch[pid,mid] = 0;
		    requested_num_other_ch[pid,mid] = 0;
		    start_time[pid,mid] = 0;
		    is_scheduled[pid,mid] = false;

		}
	    }
	    Console.WriteLine("HWA QoS, quantum:{0}, min_dead_id:{1}", quantum, min_deadLine_pid);
	    schedule_each_deadline(min_deadLine_pid, null); // initialize

	}

	public bool is_min_deadline(int pid)
	{
	    return (pid==min_deadLine_pid);
	}

	override public void schedule_each_deadline(int id, Trace m_trace)
	{
	    if( !is_min_deadline(id) ) return;

	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		for( int i = 0; i < numMemCtrls*numChannels; i++ )
		{
		    requested_num_per_ch[pid,i] = 0;
		    requested_num_other_ch[pid,i] = 0;
		    consumed_bw_per_ch[pid,i] = 0;
		    is_scheduled[pid,i] = false;
		}

		if( !Simulator.network.nodes[pid].cpu.is_HWA() )
		{
		    set_start_time_all_ch(pid,0);
		    inst_num_this_period[pid] = 0;
		}
		else
		{
		    quantum_end = Simulator.CurrentRound + quantum;
		    ulong exe_inst_num;

		    if( Config.sched.qosReGetInstNumEachDeadline )
		    {
			ulong deadLineEndTime;
			Console.WriteLine("HWA_id:{0}, achieved:{1}/{2}, deadLinePass:{3}->{4}", pid, Simulator.network.nodes[pid].cpu.deadLineReqCnt, tgt_inst_num[pid], 
					  pre_deadLinePassNum[pid], Simulator.network.nodes[pid].cpu.deadLinePassNum );

			deadLineEndTime = Simulator.network.nodes[pid].cpu.deadLineCnt + deadLine[pid];
			if( quantum_end > deadLineEndTime ) // deadline is included this quantum 
			{
			    exe_inst_num = Simulator.network.nodes[pid].cpu.deadLineReq - Simulator.network.nodes[pid].cpu.deadLineReqCnt; // by deadline
			    exe_inst_num += (ulong)Math.Ceiling((double)Simulator.network.nodes[pid].cpu.deadLineReq
								/ (double)Simulator.network.nodes[pid].cpu.deadLine * 
								(double)(quantum_end - deadLineEndTime )); // after deadline
			}
			else
			{
			    exe_inst_num = (ulong)Math.Ceiling((double)(Simulator.network.nodes[pid].cpu.deadLineReq-Simulator.network.nodes[pid].cpu.deadLineReqCnt)/
								(double)(deadLineEndTime - Simulator.CurrentRound) * (double)quantum );
			}
			tgt_inst_num[pid] = Simulator.network.nodes[pid].cpu.deadLineReqCnt + exe_inst_num;

			if( exe_inst_num > 0 )
			{
			    for( int i = 0; i < numMemCtrls*numChannels; i++ )			
				is_scheduled[pid,i] = true;
			}
		    }
		    else
		    {
			exe_inst_num = (ulong)Math.Ceiling((double)Simulator.network.nodes[pid].cpu.deadLineReq
							   / (double)Simulator.network.nodes[pid].cpu.deadLine * (double)quantum );
			
		    
			//		    Console.WriteLine("HWA_id:{4},Exe:{0}, prev_tgt:{1}, achieved:{2}, pre_inst_num:{3}",exe_inst_num,tgt_inst_num[pid],Simulator.network.nodes[pid].cpu.deadLineReqCnt,pre_inst_num[pid], pid);
			Console.WriteLine("HWA_id:{0}, achieved:{1}/{2}, deadLinePass:{3}->{4}", pid, Simulator.network.nodes[pid].cpu.deadLineReqCnt, tgt_inst_num[pid], 
					  pre_deadLinePassNum[pid], Simulator.network.nodes[pid].cpu.deadLinePassNum );
			if( Simulator.network.nodes[pid].cpu.deadLineReqCnt >= Simulator.network.nodes[pid].cpu.deadLineReq ) // already have executed all instruction
			{	
			    tgt_inst_num[pid] = Simulator.network.nodes[pid].cpu.deadLineReq;
			    exe_inst_num = 0;
			}
			else if( pre_deadLinePassNum[pid] < Simulator.network.nodes[pid].cpu.deadLinePassNum ) // stepping over deadline
			{	
			    tgt_inst_num[pid] = (ulong)Math.Ceiling((double)Simulator.network.nodes[pid].cpu.deadLineReq
								    / (double)Simulator.network.nodes[pid].cpu.deadLine * 
								    ((double)Simulator.CurrentRound - (double)Simulator.network.nodes[pid].cpu.deadLineCnt));
			    tgt_inst_num[pid] += exe_inst_num;
			    //			tgt_inst_num[pid] = Simulator.network.nodes[pid].cpu.deadLineReqCnt + exe_inst_num;
			}
			else // not stepping over deadline
			    tgt_inst_num[pid] += exe_inst_num;

			if( tgt_inst_num[pid] > Simulator.network.nodes[pid].cpu.deadLineReqCnt )
			    exe_inst_num = tgt_inst_num[pid] - Simulator.network.nodes[pid].cpu.deadLineReqCnt;
			else
			    exe_inst_num = 0;

		    }
		    pre_inst_num[pid] = Simulator.network.nodes[pid].cpu.deadLineReqCnt;
		    pre_deadLinePassNum[pid] = Simulator.network.nodes[pid].cpu.deadLinePassNum;

		    required_time[pid] = get_required_time( pid, exe_inst_num );
		    inst_num_this_period[pid] = exe_inst_num;
		    for( int i = 0; i < numMemCtrls*numChannels; i++ )			
			is_scheduled[pid,i] = true;

		    Console.WriteLine("HWA QoS: HWAid:{0}, exe_inst_num:{1}, required_time:{2}, tgt_inst_num:{3}", pid, exe_inst_num, required_time[pid], tgt_inst_num[pid]);
		}
	    }
	    calculate_start_time(quantum_end);
	}

	public ulong get_required_time( int pid, ulong exe_inst_num )
	{
	    if( exe_inst_num == 0 ) return 0;
	    if( Config.sched.qosWorstCaseStrict )
		return(cRC * exe_inst_num);
	    else
	    {
		if( pid == 17 )
		    return(cRC * exe_inst_num);
		else if( pid == 18 )
		    return(cRC * 3 + 4 * 4 * exe_inst_num);
		else
		    return(cRC + 4 * 4 * exe_inst_num);
	    }
	    
	}
	public void set_start_time_all_ch( int pid, ulong time )
	{
	    for( int mid = 0; mid < numMemCtrls; mid++ )
	    {
		for( int cid = 0; cid < numChannels; cid++ )
		{
		    start_time[pid,mid*numChannels+cid] = time;
		}
	    }
	}

	public void calculate_start_time( ulong end_time )
	{
	    bool isScheduleEnable = true;

	    for( int r = 0; r < Config.HWANum; r++ )
	    {
		int pid = hwa_rm_rank[r];
		if( pid >= 0 )
		{
		    if(( end_time - Simulator.CurrentRound ) < required_time[pid] )
		    {
			end_time = Simulator.CurrentRound;
			isScheduleEnable = false;
		    }
		    else
			end_time = end_time - required_time[pid];

		    set_start_time_all_ch(pid,end_time);

		    Console.WriteLine("HWA QoS: HWA_id:{0}, start:{1}, quauntum_end:{2}", pid, end_time, Simulator.CurrentRound+quantum);
		}
	    }
	    if( isScheduleEnable )
		Console.WriteLine("---Schedule Enable---");
	    for( int i = 0; i < numMemCtrls*numChannels; i++ )
		is_enable_schedule[i] = isScheduleEnable;
	}

	public void calculate_start_time( ulong end_time, int mid, int cid )
	{
	    bool isScheduleEnable = true;

	    for( int r = 0; r < Config.HWANum; r++ )
	    {
		int pid = hwa_rm_rank[r];
		if( pid >= 0 )
		{
		    if(( end_time - Simulator.CurrentRound ) < required_time[pid] )
		    {	
			end_time = Simulator.CurrentRound;
			isScheduleEnable = false;
		    }
		    else
			end_time = end_time - required_time[pid];

		    start_time[pid,mid*numChannels+cid] = end_time;

		    Console.WriteLine("HWA QoS: HWA_id:{0}, start:{1}, quauntum_end:{2}", pid, end_time, Simulator.CurrentRound+quantum);
		}
	    }
	    if( isScheduleEnable )
		Console.WriteLine("---Schedule Enable---");

	    is_enable_schedule[mid*numChannels+cid] = isScheduleEnable;
	}

	override public void adjust_each_epoch()
	{
	    ulong remaining_inst_num;
	    Console.WriteLine("Adjust:{0}", Simulator.CurrentRound);

	    if( Config.sched.isQosOtherChExcluded )
	    {
		for( int mid = 0; mid < numMemCtrls; mid++ )
		{
		    for( int cid = 0; cid < numChannels; cid++ )
		    {
			Console.WriteLine("Mid:{0},Cid:{1}", mid, cid );
			for( int pid = 0; pid < Config.Ng; pid++ )
			{
			    if( Simulator.network.nodes[pid].cpu.is_HWA() )
			    {
				Console.WriteLine("HWA_id:{0}, achieved:{4}(myself:{1}+other:{3})/{2}", pid, 
						  consumed_bw_per_ch[pid,mid*numChannels+cid],
						  inst_num_this_period[pid],
						  requested_num_other_ch[pid,mid*numChannels+cid],
						  consumed_bw_per_ch[pid,mid*numChannels+cid]+requested_num_other_ch[pid,mid*numChannels+cid] );
						  
				if( inst_num_this_period[pid] < ( consumed_bw_per_ch[pid,mid*numChannels+cid] + requested_num_other_ch[pid,mid*numChannels+cid] ) )
				    remaining_inst_num = 0;
				else 
				    remaining_inst_num = inst_num_this_period[pid] - consumed_bw_per_ch[pid,mid*numChannels+cid] - requested_num_other_ch[pid,mid*numChannels+cid];
				required_time[pid] = get_required_time(pid,remaining_inst_num);

				if(( remaining_inst_num == 0 ) && Config.sched.qosReGetInstNumEachDeadline && is_enable_schedule[mid*numChannels+cid] )
				    is_scheduled[pid,mid*numChannels+cid] = false;
			    }
			}
			calculate_start_time(quantum_end, mid, cid);
		    }
		}
	    }
	    else
	    {
		for( int pid = 0; pid < Config.Ng; pid++ )
		{
		    if( Config.sched.qosReGetInstNumEachDeadline )
		    {	
			if( pre_deadLinePassNum[pid] < Simulator.network.nodes[pid].cpu.deadLinePassNum ) // stepping over deadline
			{	
			    if( tgt_inst_num[pid] >= Simulator.network.nodes[pid].cpu.deadLineReqCnt + deadLineReq[pid] ) 
				remaining_inst_num = tgt_inst_num[pid] - Simulator.network.nodes[pid].cpu.deadLineReqCnt - deadLineReq[pid];
			    else 
				remaining_inst_num = 0;
			}
			else if( tgt_inst_num[pid] > Simulator.network.nodes[pid].cpu.deadLineReqCnt ) // not stepping over deadline
			    remaining_inst_num = tgt_inst_num[pid] - Simulator.network.nodes[pid].cpu.deadLineReqCnt;
			else 
			    remaining_inst_num = 0;
		    }
		    else
		    {
			if( deadLine[pid] > 0 )
			    Console.WriteLine("HWA_id:{0}, achieved:{1}/{2}, deadLine:{3}->{4}", pid, Simulator.network.nodes[pid].cpu.deadLineReqCnt, 
					      tgt_inst_num[pid], pre_deadLinePassNum[pid], Simulator.network.nodes[pid].cpu.deadLinePassNum);
			if( Simulator.network.nodes[pid].cpu.deadLineReqCnt >= Simulator.network.nodes[pid].cpu.deadLineReq ) // already have executed all instruction
			    remaining_inst_num = 0;
			else if( pre_deadLinePassNum[pid] < Simulator.network.nodes[pid].cpu.deadLinePassNum ) // stepping over deadline
			{
			    if( tgt_inst_num[pid] >= Simulator.network.nodes[pid].cpu.deadLineReqCnt + deadLineReq[pid] )
				remaining_inst_num = tgt_inst_num[pid] - Simulator.network.nodes[pid].cpu.deadLineReqCnt - deadLineReq[pid];
			    else 
				remaining_inst_num = 0;
			}
			else if( tgt_inst_num[pid] > Simulator.network.nodes[pid].cpu.deadLineReqCnt ) // not stepping over deadline
			    remaining_inst_num = tgt_inst_num[pid] - Simulator.network.nodes[pid].cpu.deadLineReqCnt;
			else 
			    remaining_inst_num = 0;
		    }

		    required_time[pid] = get_required_time(pid,remaining_inst_num);
		    /*
		    if( remaining_inst_num > 0 )
		    {
			for( int i = 0; i < numMemCtrls*numChannels; i++ )
			{
			    is_scheduled[pid,i] = true;
			}
		    }
		    else
		    {
			for( int i = 0; i < numMemCtrls*numChannels; i++ )
			{
			    is_scheduled[pid,i] = false;
			}
		    }
		    */
		}
		calculate_start_time(quantum_end);
	    }
	}

	public override bool schedule_ready( int pid, int mid, int cid )
	{
	    if( start_time[pid,mid*numChannels+cid] == 0 ) return true;
	    else if( inst_num_this_period[pid] == 0 ) return false;
	    else if( !is_scheduled[pid,mid*numChannels+cid] ) return false;
	    else if( Simulator.CurrentRound > start_time[pid,mid*numChannels+cid] ) return true;
	    else if( Simulator.CurrentRound > ( start_time[pid,mid*numChannels+cid] - cRC )) return true;
	    else return false;
	}

	public override int getHwaRank( int pid )
	{
	    return(get_hwa_rm_rank(pid));
	}

	override public void calculate_priority(int mid, int cid)
	{
	    for( int i = 0; i < Config.Ng; i++ )
	    {
		if( Simulator.network.nodes[i].cpu.is_HWA() )
		{
		    if( Simulator.QoSCtrl.schedule_ready(i,mid,cid) ) 
			priority[i] = Config.HWANum+1+Simulator.QoSCtrl.getHwaRank(i);
		    else 
			priority[i] = Simulator.QoSCtrl.getHwaRank(i);
		}
		else 
		    priority[i] = Config.HWANum;

	    }
	    return;
	}
	override public void issue_request( int pid, int ctrl_id, int ch_id, bool isImmediate )
	{
	    if( pid == 17 )
		Console.WriteLine("Issue_request precheck:{0}, ctrl_id:{1}, ch_id:{2}", isImmediate, ctrl_id, ch_id);
	    requested_num_per_ch[pid,ctrl_id*numChannels+ch_id]++;
	    if( isImmediate )
	    {
		for( int i = 0; i < numMemCtrls; i++ )
		{
		    for( int j = 0; j < numChannels; j++ )
		    {
			if(( i != ctrl_id ) || ( j != ch_id ))
			    requested_num_other_ch[pid,i*numChannels+j]++;
		    }
		}
	    }
	    else
	    {
		ulong sum = 0;
		for( int i = 0; i < numMemCtrls; i++ )
		{
		    for( int j = 0; j < numChannels; j++ )
		    {
			if(( i != ctrl_id ) || ( j != ch_id ))
			    sum += requested_num_per_ch[pid,i*numChannels+j];
		    }
		}
		requested_num_other_ch[pid,ctrl_id*numChannels+ch_id] = sum;
	    }
	    /*
	    if( pid == 17 )
	    {
		Console.WriteLine("requested_num_per_ch:{0},{1},{2},{3}", requested_num_per_ch[17,0],
				  requested_num_per_ch[17,1],
				  requested_num_per_ch[17,2],
				  requested_num_per_ch[17,3] );
		Console.WriteLine("requested_num_other_ch:{0},{1},{2},{3}", requested_num_other_ch[17,0],
				  requested_num_other_ch[17,1],
				  requested_num_other_ch[17,2],
				  requested_num_other_ch[17,3] );
		
	    }
	    */
	}
    }

    public class QoSBandwidth : HWAQoSCtrl
    {
	int [] probability;
	int [] org_probability;
	int remaining_budget;
	double [] required_bw;
	Random cRandom;
	ulong rec_Round;

	public QoSBandwidth()
	{
	    probability = new int[Config.Ng];
	    org_probability = new int[Config.Ng];
	    required_bw = new double[Config.Ng];
	    cRandom = new System.Random();

	    rec_Round = ulong.MaxValue;
	    allocate_bandwidth();
	}

	public void allocate_bandwidth()
	{
	    double total_bw = Config.sched.effective_bw_ratio / (double)4 / (double)Config.memory.busRatio * Config.memory.numMemControllers * Config.memory.numChannels;

	    remaining_budget = 1000;
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {	
		if( deadLine[pid] == 0 )
		    probability[pid] = 0;
		else
		{
		    required_bw[pid] = (double)deadLineReq[pid]/(double)deadLine[pid];
		    probability[pid] = (int)Math.Ceiling(required_bw[pid] / total_bw * 1000);
		    remaining_budget -= probability[pid];
		    org_probability[pid] = probability[pid];
		    Console.WriteLine("Prob{0}:{1}, req:{2}GB/s, total:{3}GB/s",pid,probability[pid],required_bw[pid]*64*2666666667/1024/1024/1024,total_bw*64*2666666667/1024/1024/1024);
		}
	    }
	}
	override public void adjust_each_epoch()
	{
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		if( required_bw[pid] > 0 )
		{
		    if( Simulator.CurrentRound < deadLine[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt )
			check_bandwidth(pid, Simulator.CurrentRound-Simulator.network.nodes[pid].cpu.deadLineCnt);
		    else
		    {
//			Console.WriteLine("id:{2}, deadline is over {0}/{1}", Simulator.CurrentRound, deadLine[pid] + Simulator.network.nodes[pid].cpu.deadLineCnt, pid );
			check_bandwidth(pid, Simulator.network.nodes[pid].cpu.deadLine);
		    }

//		    check_bandwidth(pid, Simulator.CurrentRound);
		}
	    }
	}
	
	public void check_bandwidth( int pid, ulong period )
	{
	    if( consumed_bw[pid] < period * required_bw[pid] )
	    {
		if( remaining_budget >= Config.sched.unitProbability*10 )
		{
		    probability[pid] += Config.sched.unitProbability*10;
		    remaining_budget -= Config.sched.unitProbability*10;
		}
	    }
	    else if ( consumed_bw[pid] > period * required_bw[pid] )
	    {
		if( probability[pid] > Config.sched.unitProbability*10 )
		{
		    probability[pid] -= Config.sched.unitProbability*10;
		    remaining_budget += Config.sched.unitProbability*10;
		}
	    }

//	    Console.WriteLine("Adjust{0}/cycle:{4}: tgt:{1}, actual:{2}, result:{3}", pid, required_bw[pid]*period, consumed_bw[pid], probability[pid], Simulator.CurrentRound);
//	    consumed_bw[pid] = 0;
	}
	override public void schedule_each_deadline( int id, Trace m_trace )
	{
	    consumed_bw[id] = 0;
	}
	override public void calculate_priority(int mid, int cid)
	{
	    if( rec_Round == Simulator.CurrentRound ) return;

	    int value = cRandom.Next(0,1000);
	    int min = 0;
	    int max = 0;
	    for( int pid = 0; pid < Config.Ng; pid++ )
	    {
		priority[pid] = 0;
		if( probability[pid] != 0 )
		{
		    max += probability[pid];
		    if(( min <= value ) && ( value < max ))
		    {
			priority[pid] = 1;
//			Console.WriteLine("Highest:{0}/{1}", pid, value);
		    }
		    min = max;

		    if( Config.sched.hwa_emergency_progress > 0 )
			if( isEmergency(pid,Config.sched.hwa_emergency_progress) )
			    priority[pid] = 2;
		}
	    }
	    rec_Round = Simulator.CurrentRound;
	}
//	override public int get_priority( int pid, bool marked )
//	{
//	    if( marked ) return(2);
//	    else return(priority[pid]);
//	}
    }	
}
