using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedCTCM : SchedTCM
    {
        public SchedCTCM(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
        }


        override public void decay_stats()
        {
            for (int p = 0; p < Config.Ng; p++) {
                ulong cache_miss = Simulator.stats.L2_misses_persrc[p].Count;
                ulong delta_cache_miss = cache_miss - prev_cache_miss[p];
                prev_cache_miss[p] = cache_miss;

                ulong inst_cnt = Simulator.stats.insns_persrc[p].Count;
                ulong delta_inst_cnt = inst_cnt - prev_inst_cnt[p];
                prev_inst_cnt[p] = inst_cnt;

                //mpki
                double curr_mpki = 1000 * ((double)delta_cache_miss) / delta_inst_cnt;
                //GPU
                if(p==(Config.Ng-1))
                    curr_mpki = 150;
                mpki[p] = Config.sched.history_weight * mpki[p] + (1 - Config.sched.history_weight) * curr_mpki;

                //rbl
                double curr_rbl = ((double)shadow_row_hits[p]) / delta_cache_miss;
                rbl[p] = Config.sched.history_weight * rbl[p] + (1 - Config.sched.history_weight) * curr_rbl;
                shadow_row_hits[p] = 0;

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


    }
}
