package com.seleuco.mame4droid.widgets;

import android.content.pm.ActivityInfo;
import android.graphics.Color;
import android.util.DisplayMetrics;
import android.view.Gravity;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.locks.ReentrantLock;

import com.seleuco.mame4droid.MAME4droid;
import com.seleuco.mame4droid.R;

public class WarnWidget {

	public static class WarnWidgetHelper extends Thread{
		WarnWidget warnWidget;
		int time;

		/* One notice on screen at a time: they used to be drawn on top of each
		 * other, so one arriving buried the last and was itself half readable.
		 * Fair, so they come out in the order they queued. */
		private static final ReentrantLock turn = new ReentrantLock(true);

		/* A queue that never hurries turns the last notice into old news. The
		 * whole backlog gets this long: each spends what is left of it, split
		 * with whoever waits, down to a floor that can still be read. */
		private static final long QueueBudgetMs = 7000L;
		private static final int MinShownSeconds = 1;

		/* Waiting a turn is right for what just happened and wrong for what is
		 * happening NOW: "resyncing" used to arrive once the resync was over,
		 * leaving the freeze unexplained. Urgent notices clear the screen. */
		private static final AtomicInteger urgentWaiting = new AtomicInteger(0);
		/* Between two urgent ones the newer wins: live status has one value. */
		private static final AtomicInteger urgentGen = new AtomicInteger(0);
		private static final long SliceMs = 50L;
		/* How many turns a notice will give away before taking its own. */
		private static final int MaxWaivedTurns = 40;
		/* Nothing is cleared before this: a notice that appears and vanishes
		 * again is worse than the wait it saves. */
		private static final long MinBeforeCutMs = 1200L;

		private final long queuedAt = System.currentTimeMillis();
		private final boolean urgent;
		private final int gen;

		public WarnWidgetHelper(MAME4droid mm, String msg,int time,int color, boolean bottom){
			this(mm, msg, time, color, bottom, false);
		}

		public WarnWidgetHelper(MAME4droid mm, String msg,int time,int color, boolean bottom, boolean urgent){
			warnWidget = new WarnWidget(mm, "", msg,color,bottom,false);
			this.time = time;
			this.urgent = urgent;
			this.gen = urgent ? urgentGen.incrementAndGet() : 0;
			/* Announced before the thread starts: whoever is on screen sees it
			 * on its next slice and steps off without waiting to be asked. */
			if (urgent) urgentWaiting.incrementAndGet();
			/* init() moved into run(): showing it here would put it on screen
			 * while an earlier notice is still up, which is the bug. */
			this.start();
		}

		@Override
		public void run() {
			/* Step aside while an urgent notice waits. The lock is fair, so
			 * releasing puts us at the back and the urgent one moves up. A
			 * slice and not a yield: the count rises before that thread
			 * starts, and yielding into that gap is a hot spin. */
			for (int waived = 0; ; waived++) {
				turn.lock();
				/* Bounded, because a count that never came back down would
				 * silence every notice from here on. */
				if (urgent || urgentWaiting.get() == 0 || waived >= MaxWaivedTurns) break;
				turn.unlock();
				try {
					Thread.sleep(SliceMs);
				} catch (InterruptedException e) {
					Thread.currentThread().interrupt();
					return;
				}
			}
			try {
				if (urgent) urgentWaiting.decrementAndGet();

				/* What the budget has left after our wait, shared with whoever
				 * is still queued: alone that is all of it, and in a burst it
				 * shrinks so the last one is still recent when it lands. */
				long left = Math.max(0L, QueueBudgetMs - (System.currentTimeMillis() - queuedAt));
				long share = left / (turn.getQueueLength() + 1);
				int shown = Math.max(MinShownSeconds, Math.min(time, (int) (share / 1000L)));

				warnWidget.init();
				/* Slept in slices so the screen can be given up early: to an
				 * urgent notice arriving, or to a newer urgent one superseding
				 * this. Never before it has been up long enough to read. */
				long shownAt = System.currentTimeMillis();
				long until = shownAt + 1000L * shown;
				while (System.currentTimeMillis() < until) {
					if (System.currentTimeMillis() - shownAt >= MinBeforeCutMs
							&& (urgent ? urgentGen.get() != gen : urgentWaiting.get() > 0))
						break;
					try {
						Thread.sleep(SliceMs);
					} catch (InterruptedException e) {
						/* Throwing here killed the thread and left the notice
						 * on screen for good. Flag it and take it down. */
						Thread.currentThread().interrupt();
						break;
					}
				}
				warnWidget.end();
			} finally {
				turn.unlock();
			}
		}
	}

	protected MAME4droid mm;

	protected String title = null;
	protected String initMsg = null;
	protected int color;
	protected long initTime;
	protected long lastTime;
	protected TextView textView = null;
	protected LinearLayout parent = null;
	/* Written on the UI thread, read from the thread that shows the notice:
	 * without volatile the wait in end() can spin on a stale copy. */
	protected volatile boolean init = false;
	protected volatile boolean added = false;
	protected boolean lockOrientation = true;
	protected boolean bottom = false;

	protected int orientation;

	public WarnWidget(MAME4droid mm, String title, String initMsg,int color,boolean bottom, boolean lock) {
	   this.mm = mm;
	   this.title = title;
	   this.initMsg = initMsg;
	   this.color = color;
	   this.bottom = bottom;
		this.lockOrientation = lock;
	}

	public void init(){

		initTime = lastTime = System.currentTimeMillis();

		init = true;

		if(this.lockOrientation) {
			orientation = mm.getMainHelper().getScreenOrientation();
			mm.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_LOCKED);
		}

		mm.runOnUiThread(new Runnable() {
			public void run() {

				FrameLayout frame = mm.findViewById(R.id.EmulatorFrame);

				textView = new TextView(mm);

				parent = new LinearLayout(mm);
				LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.MATCH_PARENT);
				parent.setLayoutParams(params);

				float px = 50 * ((float) mm.getResources().getDisplayMetrics().densityDpi / DisplayMetrics.DENSITY_DEFAULT);

				params.setMargins((int)px,(int)px,(int)px,(int)px);
				parent.setOrientation(LinearLayout.HORIZONTAL);

				if(bottom) {
					parent.setGravity(Gravity.BOTTOM | Gravity.CENTER);
					//parent.setPadding(0,0,0,100);
				}
				else
					parent.setGravity(Gravity.CENTER);

				textView.setBackgroundResource(R.drawable.border_shape);
				textView.setTextColor(color);
				textView.setGravity(Gravity.CENTER_VERTICAL | Gravity.CENTER_HORIZONTAL);



				parent.addView(textView);
				frame.addView(parent);

				textView.setText(title+" "+initMsg);

				added = true;
			}
		});
	}
	public void notifyText(String msg){
		long currTime = System.currentTimeMillis();

		if(currTime - lastTime > 40 && textView !=null) {
			mm.runOnUiThread(new Runnable() {
				public void run() {
					try {
						textView.setText(msg);

					}catch(NullPointerException e){}
					lastTime = System.currentTimeMillis();
				}
			});
		}
	}

	public void end() {

		if (!init) return;

		/* The removal below is posted behind the runnable init() queued, so this
		 * is only a courtesy wait. Bounded because it runs holding the notice
		 * turn, and a UI thread that never got to it would wedge every later one. */
		long giveUp = System.currentTimeMillis() + 2000L;
		while (!added && System.currentTimeMillis() < giveUp) {
			try {
				Thread.sleep(10);
			} catch (InterruptedException e) {
				Thread.currentThread().interrupt();
				break;
			}
		}

		mm.runOnUiThread(new Runnable() {
			public void run() {

				FrameLayout frame = mm.findViewById(R.id.EmulatorFrame);
				frame.removeView(parent);
				textView = null;
				parent = null;
				if(lockOrientation)
				   mm.setRequestedOrientation(orientation);
			}
		});

	}
}
