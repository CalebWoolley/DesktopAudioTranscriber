
using NAudio.Wave;
//Credit goes to https://github.com/naudio/NAudio
//
//Steve Cox - 10/13/17 - All audio code comes several NAudio projects. I mashed up just the code I needed for this demo
//Steve Cox - 12/23/17 - Added timer and peak audio detector code for a simple voice activated effect


using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Google.Apis.Auth.OAuth2;
using Google.Cloud.Language.V1;
using Google.Cloud.Speech.V1;

using Grpc.Auth;
using Google.Protobuf.Collections;
using System.Threading;
using NAudio.Mixer;

//NOTE: You will have to goto "Tools->Nuget Package Manager->Packet Manager Console" and copy paste, then run the below commands
//Install-Package Google.Cloud.Speech.V1 -Version 1.0.0-beta08 -Pre
//Install-Package Google.Cloud.Language.V1 -Pre

namespace WinRecognize
{
    public partial class Form1 : Form
    {


        private List<string> recordingDevices = new List<string>();
        private AudioRecorder audioRecorder = new AudioRecorder();

        private Boolean monitoring = false;
        
        //private RecognitionConfig oneShotConfig;
        //private SpeechClient speech = SpeechClient.Create();
        //private SpeechClient.StreamingRecognizeStream streamingCall;
        //private StreamingRecognizeRequest streamingRequest;

        private BufferedWaveProvider waveBuffer;
        
        // Read from the microphone and stream to API.
        private WaveInEvent waveIn = new NAudio.Wave.WaveInEvent();
        Grpc.Core.Channel channel;
        SpeechClient speech;

        public Form1()
        {
            GoogleCredential credential = GoogleCredential.FromFile(@"authkey.json").CreateScoped(SpeechClient.DefaultScopes);
            channel = new Grpc.Core.Channel(SpeechClient.DefaultEndpoint.ToString(), credential.ToChannelCredentials());
            speech = SpeechClient.Create(channel);

            InitializeComponent();
            Rectangle workingArea = Screen.GetWorkingArea(this);
            this.Location = new Point(workingArea.Right - Size.Width,
                                      workingArea.Bottom - Size.Height);
             
            if (NAudio.Wave.WaveIn.DeviceCount < 1)
            {
                MessageBox.Show("No microphone! ... exiting");
                return;
            }

            //Mixer
            //Hook Up Audio Mic for sound peak detection
            audioRecorder.SampleAggregator.MaximumCalculated += OnRecorderMaximumCalculated;
          
            for (int n = 0; n < WaveIn.DeviceCount; n++)
            {
                recordingDevices.Add(WaveIn.GetCapabilities(n).ProductName);
            }

            //Set up Google specific code
            //oneShotConfig = new RecognitionConfig();
            //oneShotConfig.Encoding = RecognitionConfig.Types.AudioEncoding.Linear16;
            //oneShotConfig.SampleRateHertz = 16000;
            //oneShotConfig.LanguageCode = "en";



            //Set up NAudio waveIn object and events
            waveIn.DeviceNumber = 0;
            waveIn.WaveFormat = new NAudio.Wave.WaveFormat(16000, 1);
            //Need to catch this event to fill our audio beffer up
            waveIn.DataAvailable += WaveIn_DataAvailable;
            //the actuall wave buffer we will be sending to googles for voice to text conversion
            waveBuffer = new BufferedWaveProvider(waveIn.WaveFormat);
            waveBuffer.DiscardOnBufferOverflow = true;
            
            //We are using a timer object to fire a one second record interval
            //this gets enabled and disabled based on when we get a peak detection from NAudio
            timer1.Enabled = false;
            //One second record window
            timer1.Interval = 1000;
            //Hook up to timer tick event
            timer1.Tick += Timer1_Tick;

            if (recordingDevices.Count > 0)
            {
                if (monitoring == false)
                {
                    monitoring = true;
                    //Begin
                    audioRecorder.BeginMonitoring(0);
                }
                else
                {
                    monitoring = false;
                    audioRecorder.Stop();
                }


            }
        }

        System.Diagnostics.Stopwatch recorderTimer = new System.Diagnostics.Stopwatch();
        Boolean recordingVoice = false;

        /// <summary>
        /// Fires when audio peak detected. If we get a peak audio signal 
        /// above a certain threshold, start recording audio, set a timer to call us back after one second
        /// so we can stop recording and send what audio we have to googles
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void OnRecorderMaximumCalculated(object sender, MaxSampleEventArgs e)
        {   
            float peak = Math.Max(e.MaxSample, Math.Abs(e.MinSample));

            // multiply by 100 because the Progress bar's default maximum value is 100
            peak *= 100;

            //changes panel size like a progress bar to show visual for audio levels
            panel1.Size = new Size((int)peak * 10, 3);

            //Console.WriteLine("Recording Level " + peak);
            if (peak > 4)
            {
                //Timer should not be enabled, meaning, we are not already recording
                recorderTimer.Restart();
                if (recordingVoice == false)
                {
                    recordingVoice = true;
                    waveIn.StartRecording();
                }

            } else
            {
                if (recordingVoice == true)
                {
                    recorderTimer.Start();
                    if (recorderTimer.ElapsedMilliseconds > 750)
                    {
                        recorderTimer.Stop();
                        recorderTimer.Reset();
                        recordingVoice = false;

                        waveIn.StopRecording();
                         //Call the async google voice stream method with our saved audio buffer
                        Task me = StreamBufferToGooglesAsync();
                    }
                }
            }
           
        }

        /// <summary>
        /// When we get data from microphone or audio srouce, add to internal wave buffer for later use
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            waveBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        }

        /// <summary>
        /// fires after one second recording interval
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Timer1_Tick(object sender, EventArgs e)
        {
            //Turn off events, will get re-enabled once another audio peak gets detected
            timer1.Enabled = false;
            //Stop recording
            waveIn.StopRecording();
           
            //Call the async google voice stream method with our saved audio buffer
            Task me = StreamBufferToGooglesAsync();
          
        }
        
        /// <summary>
        /// Wave in recording task gets called when we think we have enough audio to send to googles
        /// </summary>
        /// <param name="seconds"></param>
        /// <returns></returns>
        private async Task<object> StreamBufferToGooglesAsync()
        {
            //I don't like having to re-create these everytime, but breaking the
            //code out is for another refactoring.
            var streamingCall = speech.StreamingRecognize();

            // Write the initial request with the config.
            //Again, this is googles code example, I tried unrolling this stuff
            //and the google api stopped working, so stays like this for now
            await streamingCall.WriteAsync(new StreamingRecognizeRequest()
            {
                StreamingConfig = new StreamingRecognitionConfig()
                {
                    Config = new RecognitionConfig()
                    {
                        Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
                        SampleRateHertz = 16000,
                        LanguageCode = LanguageCodes.English.UnitedStates,
                    },

                    //Note: play with this value
                    //InterimResults = true,  // this needs to be true for real time
                    SingleUtterance = false,
                }
            });



            //Get what ever data we have in our internal wave buffer and put into
            //byte array for googles
            byte[] buffer = new byte[waveBuffer.BufferLength];
            int offset = 0;
            int count = waveBuffer.BufferLength;

            //Gulp ... yummy bytes ....
            waveBuffer.Read(buffer, offset, count);
            try
            {
                //Sending to Googles .... finally
                streamingCall.WriteAsync(new StreamingRecognizeRequest()
                {
                    AudioContent = Google.Protobuf.ByteString.CopyFrom(buffer, 0, count)
                }).Wait();
            }
            catch (Exception wtf)
            {
                string wtfMessage = wtf.Message;
            }


            //Again, this is googles code example below, I tried unrolling this stuff
            //and the google api stopped working, so stays like this for now

            //Print responses as they arrive. Need to move this into a method for cleanslyness
            Task printResponses = Task.Run(async () =>
            {
                string saidWhat = "";
                string lastSaidWhat = "";
                while (await streamingCall.ResponseStream.MoveNext(default(CancellationToken)))
                {
                    foreach (var result in streamingCall.ResponseStream.Current.Results)
                    {
                        foreach (var alternative in result.Alternatives)
                        {
                            saidWhat = alternative.Transcript;
                            if (lastSaidWhat != saidWhat)
                            {
                                lastSaidWhat = saidWhat;
                                saidWhat = FirstCharToUpper(saidWhat);
                                saidWhat = saidWhat + FindEndPunctuation(saidWhat);
                                Console.WriteLine(saidWhat);
                                //Need to call this on UI thread ....
                                this.Invoke((MethodInvoker)delegate { textBox1.Text = textBox1.Text.Replace("...", ""); });
                                this.Invoke((MethodInvoker)delegate { textBox1.Text =  textBox1.Text + " | " + saidWhat; });

                            }

                        }  // end for

                    } // end for


                }
            });

            //Clear our internal wave buffer
            waveBuffer.ClearBuffer();

            //Tell googles we are done for now
            await streamingCall.WriteCompleteAsync();
            
            return 0;
        }
        public static string FirstCharToUpper(string input)
        {
            if (String.IsNullOrEmpty(input))
                throw new ArgumentException("ARGH!");
            return input.First().ToString().ToUpper() + input.Substring(1);
        }

        public static string FindEndPunctuation(string input)
        {
            if (input.StartsWith("Who") ^ input.StartsWith("What") ^ input.StartsWith("When") ^ input.StartsWith("Are") ^ input.StartsWith("Why") ^ input.StartsWith("How"))
            {
                return "?";
            } else
            {
                return ".";
            }
        }

        /// <summary>
        /// Starts the voice activated audio recording
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button3_Click(object sender, EventArgs e)
        {

            
        }

        private void Mypanel_Paint(object sender, PaintEventArgs e)
        {

        }

        private void Button3_Click_1(object sender, EventArgs e)
        {

        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private void ToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            textBox1.Text = "Cleared.";
        }

        private void TextBox1_TextChanged(object sender, EventArgs e)
        {
            textBox1.SelectionStart = textBox1.Text.Length;
        }

        private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
} 