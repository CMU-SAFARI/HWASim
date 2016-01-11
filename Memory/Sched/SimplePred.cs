using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SimplePredEntry
    {
	public bool valid;
	public ulong value;

	public SimplePredEntry()
	{
	    valid = false;
	    value = 0;
	}
	public void set_value( ulong s_value )
	{
	    valid = true;
	    value = s_value;
	}
	public bool get_value( out ulong o_value )
	{
	    o_value = value;
	    return(valid);
	}
    }
    public class SimplePredictor
    {
	public SimplePredEntry[] pred_entries;
	int entry_num;
	int lsb_bit;

	public SimplePredictor( int set_entry_num, int set_lsb_bit )
	{
	    entry_num = set_entry_num;
	    lsb_bit = set_lsb_bit;

	    pred_entries = new SimplePredEntry[entry_num];

	    for( int i = 0; i < entry_num; i++ )
	    {
		pred_entries[i] = new SimplePredEntry();
	    }
	}
	public void setValue( ulong address, ulong s_value )
	{
	    int entry_no = getEntry(address);
	    pred_entries[entry_no].set_value(s_value);
	}
	public bool getValue( ulong address, out ulong o_value )
	{
	    int entry_no = getEntry(address);
	    bool flag = pred_entries[entry_no].get_value(out o_value);
	    return(flag);
	}
	public int getEntry( ulong address )
	{
	    ulong entry = (address >> lsb_bit) % (ulong)entry_num;
	    return((int)entry);
	}
    }
}