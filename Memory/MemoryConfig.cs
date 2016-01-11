using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Globalization;

namespace ICSimulator
{
    public class MemoryConfig : ConfigGroup
    {
        public AddressMap address_mapping = AddressMap.BMR;
        
        public ChannelMapping channelMapping = new ChannelMapping();
         
        public List<Coord> MCLocations = new List<Coord>(); //<summary> List of node coordinates with an MC </summary>

        // DRAM Parameters
        public int DRAMRowSize=1024;
        public int numMemControllers=2;
        public int numChannels=1;
        public int numRanks=2;
        public int numBanks=8;

        public uint busWidth = 8; // 64-bit
        public uint busRatio = 4; // if CPU speed is 4GHz, then memory speed is 1GHz/2GHz-DDR
        
        public int mem_bit;
        public int channel_bit;
        public int rank_bit;
        public int bank_bit;
        public int row_bit;

        // SDRAM Timing Parameters - all in CPU clock cycles
        public uint cRAS = 80; // ACT to PRE
        public uint cCAS = 24; // RD to DATA
        public uint cWR = 24;  // WR to PRE
        public uint cDQS = 8;  // WR to DQ
        public uint cWTR = 16; // WR to RD
        public uint cRCD = 24; // ACT to RD/WR
        public uint cRP = 24;  // PRE to idle
        public uint cRTP = 16; // RD to PRE
        public uint cRC = 108;  // ACT to ACT (same bank)
        public uint cRRD = 16; // ACT to ACT (same rank)

        // DCT Memory controller parameters
        public int schedBufSize = 64;
        public int reservedCoreEntries = 16;
        public int reservedGPUEntries = 16;
        public int RDBSize = 64;
        public int WDBSize = 64;
	/* HWA Code */
	public int reservedHWAEntries = 16;
	/* HWA Code End */

        public int coreUrgentThreshold = 512;
        public int GPUUrgentThreshold = 2048;
        public int SuperUrgentThreshold = 8192;
 
        public int coreBatchUrgencyThreshold = 512;
        public int GPUBatchUrgencyThreshold = 2048;
        public int batchSuperUrgencyThreshold = 8192;

        public string batcherPolicy = "SimpleBatcher";
        public string batchSchedulerPolicy = "SimpleScheduler";

        public bool DRAMProactiveClose = false;
        public ulong DRAMPREMinAge = 4096;

        public string DCTARBPolicy = "GFRFCFS";
        public int PARBSBatchCap = 5;

        // MemoryCoalescing Parameters
        public int MemoryCoalescingAgeWeight = 1;
        public int MemoryCoalescingCountWeight = 32;
        public int MemoryCoalescingStreakMax = 16;
        public int MemoryCoalescingRWSwitchThreshold = 128;
        public int MemoryCoalescingUrgentThreshold = 2048;
        public int MemoryCoalescingLazyThreshold = 4;
        public int MemoryCoalescingCombineSize = 256;
        public int MemoryCoalescingComboMax = int.MaxValue;

        protected override bool setSpecialParameter(string flag_type, string flag_val)
        {
//            string[] values = new string[Config.Ng];
//            char[] splitter = { ',' };

            // TODO: this is meaningless right now
            switch (flag_type)
            {
                case "AddressMap":
                    switch (flag_val) {
                        case "BMR":
                            address_mapping = AddressMap.BMR; break;
                        case "BRM":
                            address_mapping = AddressMap.BRM; break;
                        case "MBR":
                            address_mapping = AddressMap.MBR; break;
                        case "MRB":
                            address_mapping = AddressMap.MRB; break;
                        default:
                            Console.WriteLine("AddressMap " + flag_val + " not found");
                            Environment.Exit(-1);
                            break;
                    }
                    break;
                
                default:
                    return false;
            }
            return true;
        }

        public override void finalize()
        {
            row_bit = (int)Math.Ceiling(Math.Log(DRAMRowSize,2.0));

            mem_bit = 0;
            channel_bit = (int)Math.Ceiling(Math.Log(numMemControllers,2.0));
            rank_bit = channel_bit + (int)Math.Ceiling(Math.Log(numChannels,2.0));
            bank_bit = rank_bit + (int)Math.Ceiling(Math.Log(numRanks,2.0));

		    MCLocations.Add(new Coord(0, 0));
			if(numMemControllers == 1) return;
			
            MCLocations.Add(new Coord(0, Config.network_nrY - 1));
			if(numMemControllers == 2) return;
			
            MCLocations.Add(new Coord(Config.network_nrX - 1, 0));
			if(numMemControllers == 3) return;
			
            MCLocations.Add(new Coord(Config.network_nrX - 1, Config.network_nrY - 1));
			if(numMemControllers == 4) return;

/*            MCLocations.Add(new Coord(Config.network_nrX - 1, Config.network_nrY - 4));
			if(numMemControllers == 1) return;
			
            MCLocations.Add(new Coord(Config.network_nrX - 1, Config.network_nrY - 3));
			if(numMemControllers == 2) return;
			
            MCLocations.Add(new Coord(Config.network_nrX - 1, Config.network_nrY - 2));
			if(numMemControllers == 3) return;
			
            MCLocations.Add(new Coord(Config.network_nrX - 1, Config.network_nrY - 1));
			if(numMemControllers == 4) return;
*/			
        }

        public void parseMCLocations(string mcLocationsToBeConverted)
        {
            Char[] delims = new Char[] { '(', ',', ' ', ')' };
            string[] split = mcLocationsToBeConverted.Split(delims);
            for (int i = 1; i < split.Length - 1; i += 4)
            {
                MCLocations.Add(new Coord(Int32.Parse(split[i]), Int32.Parse(split[i + 1])));
            }
        }
    }
}
