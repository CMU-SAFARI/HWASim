using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class PhasePredEntry
    {
	public bool valid;
	public ulong worst_latency;

	public void set_latency( ulong latency )
	{
	    valid = true;
	    worst_latency = latency;
	}
    }
    public class PhasePredictor
    {
	public PhasePredEntry[] pred_entries;
	double pre_ratio;
	ulong worst_latency;
	int entry_num;
	int lsb_bit;

	public PhasePredictor( int set_entry_num, ulong set_worst_latency, int set_lsb_bit )
	{
	    entry_num = set_entry_num;
	    worst_latency = set_worst_latency;
	    lsb_bit = set_lsb_bit;

	    pred_entries = new PhasePredEntry[entry_num];
	    for( int i = 0; i < entry_num; i++ )
	    {
		pred_entries[i] = new PhasePredEntry();
		pred_entries[i].set_latency( worst_latency );
	    }

	    pre_ratio = 0.8;
	}
	public ulong getMaxLatency( ulong address )
	{
	    int entry_no = getEntry(address);
	    if( pred_entries[entry_no].valid ) return(pred_entries[entry_no].worst_latency);
	    else return 0;
	}
	public void setMaxLatency( ulong address, ulong latency )
	{
	    int entry_no = getEntry(address);
	    ulong modified_latency = (ulong)(pred_entries[entry_no].worst_latency * pre_ratio + latency * ( 1 - pre_ratio ));

//	    Console.WriteLine("pred_update id:{0}, pre:{1}, next:{2}", entry_no, pred_entries[entry_no].worst_latency, modified_latency);

	    pred_entries[entry_no].set_latency(modified_latency);
	}
	public int getEntry( ulong address )
	{
	    return ((int)((address >> lsb_bit) % (ulong)entry_num) );
	}
    }
}